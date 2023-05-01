﻿// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the MIT License. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Microsoft.ApplicationInsights;
using Microsoft.ApplicationInsights.Channel;
using Microsoft.ApplicationInsights.DataContracts;
using Microsoft.ApplicationInsights.Extensibility;
using Microsoft.ApplicationInsights.Extensibility.Implementation;
using Microsoft.ApplicationInsights.WindowsServer.TelemetryChannel;
using Microsoft.Azure.WebJobs.Host;
using Microsoft.Azure.WebJobs.Host.Executors;
using Microsoft.Azure.WebJobs.Host.Scale;
using Microsoft.Azure.WebJobs.Hosting;
using Microsoft.Azure.WebJobs.Logging;
using Microsoft.Azure.WebJobs.Logging.ApplicationInsights;
using Microsoft.Azure.WebJobs.Script.Binding;
using Microsoft.Azure.WebJobs.Script.BindingExtensions;
using Microsoft.Azure.WebJobs.Script.Config;
using Microsoft.Azure.WebJobs.Script.Configuration;
using Microsoft.Azure.WebJobs.Script.DependencyInjection;
using Microsoft.Azure.WebJobs.Script.Description;
using Microsoft.Azure.WebJobs.Script.Diagnostics;
using Microsoft.Azure.WebJobs.Script.Eventing;
using Microsoft.Azure.WebJobs.Script.Extensibility;
using Microsoft.Azure.WebJobs.Script.ExtensionBundle;
using Microsoft.Azure.WebJobs.Script.FileProvisioning;
using Microsoft.Azure.WebJobs.Script.Http;
using Microsoft.Azure.WebJobs.Script.ManagedDependencies;
using Microsoft.Azure.WebJobs.Script.Scale;
using Microsoft.Azure.WebJobs.Script.Workers;
using Microsoft.Azure.WebJobs.Script.Workers.Http;
using Microsoft.Azure.WebJobs.Script.Workers.Profiles;
using Microsoft.Azure.WebJobs.Script.Workers.Rpc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Configuration;
using Microsoft.Extensions.Options;

namespace Microsoft.Azure.WebJobs.Script
{
    public static class ScriptHostBuilderExtensions
    {
        private const string BundleManagerKey = "MS_BundleManager";
        private const string StartupTypeLocatorKey = "MS_StartupTypeLocator";
        private const string ChannelManager = "MS_ChannelManager";
        private const string DelayedConfigurationActionKey = "MS_DelayedConfigurationAction";
        private const string ConfigurationSnapshotKey = "MS_ConfigurationSnapshot";

        public static IHostBuilder AddScriptHost(this IHostBuilder builder, Action<ScriptApplicationHostOptions> configureOptions, ILoggerFactory loggerFactory = null)
        {
            if (configureOptions == null)
            {
                throw new ArgumentNullException(nameof(configureOptions));
            }

            ScriptApplicationHostOptions options = new ScriptApplicationHostOptions();

            configureOptions(options);

            return builder.AddScriptHost(options, loggerFactory, null);
        }

        public static IHostBuilder AddScriptHost(this IHostBuilder builder,
                                                 ScriptApplicationHostOptions applicationOptions,
                                                 Action<IWebJobsBuilder> configureWebJobs = null,
                                                 IMetricsLogger metricsLogger = null)
            => builder.AddScriptHost(applicationOptions, null, metricsLogger, configureWebJobs);

        public static IHostBuilder AddScriptHost(this IHostBuilder builder,
                                                 ScriptApplicationHostOptions applicationOptions,
                                                 ILoggerFactory loggerFactory,
                                                 IMetricsLogger metricsLogger,
                                                 Action<IWebJobsBuilder> configureWebJobs = null)
        {
            loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;

            builder.SetAzureFunctionsConfigurationRoot();
            // Host configuration
            builder.ConfigureLogging((context, loggingBuilder) =>
            {
                loggingBuilder.AddDefaultWebJobsFilters();

                string loggingPath = ConfigurationPath.Combine(ConfigurationSectionNames.JobHost, "Logging");
                loggingBuilder.AddConfiguration(context.Configuration.GetSection(loggingPath));

                loggingBuilder.Services.AddSingleton<IFileWriterFactory, DefaultFileWriterFactory>();
                loggingBuilder.Services.AddSingleton<ILoggerProvider, HostFileLoggerProvider>();
                loggingBuilder.Services.AddSingleton<ILoggerProvider, FunctionFileLoggerProvider>();

                loggingBuilder.AddConsoleIfEnabled(context);

                ConfigureApplicationInsights(context, loggingBuilder);
            })
            .ConfigureAppConfiguration((context, configBuilder) =>
            {
                if (!context.Properties.ContainsKey(ScriptConstants.SkipHostJsonConfigurationKey))
                {
                    configBuilder.Add(new HostJsonFileConfigurationSource(applicationOptions, SystemEnvironment.Instance, loggerFactory, metricsLogger));
                }
                // Adding hosting config into job host configuration
                IConfiguration scriptHostConfiguration = applicationOptions.RootServiceProvider.GetService<IConfiguration>();
                if (scriptHostConfiguration != null)
                {
                    configBuilder.AddConfiguration(scriptHostConfiguration.GetSection(ScriptConstants.FunctionsHostingConfigSectionName));
                }
            });

            // WebJobs configuration
            builder.AddScriptHostCore(applicationOptions, configureWebJobs, loggerFactory);

            // Allow FunctionsStartup to add configuration after all other configuration is registered.
            builder.ConfigureAppConfiguration((context, configBuilder) =>
            {
                // Pre-build configuration here to load bundles and to store for later validation.
                var config = configBuilder.Build();
                var extensionBundleOptions = GetExtensionBundleOptions(config);
                var bundleManager = new ExtensionBundleManager(extensionBundleOptions, SystemEnvironment.Instance, loggerFactory);
                var metadataServiceManager = applicationOptions.RootServiceProvider.GetService<IFunctionMetadataManager>();
                var languageWorkerOptions = applicationOptions.RootServiceProvider.GetService<IOptionsMonitor<LanguageWorkerOptions>>();

                var eventManager = applicationOptions.RootServiceProvider.GetService<IScriptEventManager>();
                var metricsLogger = applicationOptions.RootServiceProvider.GetService<IMetricsLogger>();
                var rpcWorkerChannelFactory = applicationOptions.RootServiceProvider.GetService<IRpcWorkerChannelFactory>();
                var workerProfileManager = applicationOptions.RootServiceProvider.GetService<IWorkerProfileManager>();
                var options = applicationOptions.RootServiceProvider.GetService<IOptionsMonitor<ScriptApplicationHostOptions>>();

                var channelManager = new WebHostRpcWorkerChannelManager(eventManager,
                    SystemEnvironment.Instance,
                    loggerFactory,
                    rpcWorkerChannelFactory,
                    options,
                    metricsLogger,
                    languageWorkerOptions,
                    config,
                    workerProfileManager);

                var locator = new ScriptStartupTypeLocator(applicationOptions.ScriptPath, loggerFactory.CreateLogger<ScriptStartupTypeLocator>(), bundleManager, metadataServiceManager, metricsLogger, languageWorkerOptions, channelManager);

                // The locator (and thus the bundle manager) need to be created now in order to configure app configuration.
                // Store them so they do not need to be re-created later when configuring services.
                context.Properties[BundleManagerKey] = bundleManager;
                context.Properties[StartupTypeLocatorKey] = locator;
                context.Properties[ChannelManager] = channelManager;

                // If we're skipping host initialization, this key will not exist and this will also be skipped.
                if (context.Properties.TryGetValue(DelayedConfigurationActionKey, out object actionObject) &&
                    actionObject is Action<IWebJobsStartupTypeLocator> delayedConfigAction)
                {
                    context.Properties.Remove(DelayedConfigurationActionKey);

                    delayedConfigAction(locator);

                    // store the snapshot for validation later, but only if there
                    // are any registered external configuration startups.
                    if (locator.HasExternalConfigurationStartups())
                    {
                        context.Properties[ConfigurationSnapshotKey] = config;
                    }
                }
            });

            return builder;
        }

        public static IHostBuilder AddScriptHostCore(this IHostBuilder builder, ScriptApplicationHostOptions applicationHostOptions, Action<IWebJobsBuilder> configureWebJobs = null, ILoggerFactory loggerFactory = null)
        {
            var skipHostInitialization = builder.Properties.ContainsKey(ScriptConstants.SkipHostInitializationKey);

            builder.ConfigureServices((context, services) =>
            {
                services.AddSingleton<ExternalConfigurationStartupValidator>();
                services.AddSingleton<IHostedService>(s =>
                {
                    if (!skipHostInitialization)
                    {
                        var environment = s.GetService<IEnvironment>();

                        // This key will not be here if we don't have any external configuration startups registered
                        if (context.Properties.TryGetValue(ConfigurationSnapshotKey, out object originalConfigObject) &&
                            originalConfigObject is IConfigurationRoot originalConfig)
                        {
                            context.Properties.Remove(ConfigurationSnapshotKey);

                            // Validate the config for anything that needs the Scale Controller.
                            // Including Core Tools as a warning during development time.
                            if (environment.IsWindowsConsumption() ||
                                environment.IsAnyLinuxConsumption() ||
                                (environment.IsWindowsElasticPremium() && !environment.IsRuntimeScaleMonitoringEnabled()) ||
                                environment.IsCoreTools())
                            {
                                var validator = s.GetService<ExternalConfigurationStartupValidator>();
                                var logger = s.GetService<ILoggerFactory>().CreateLogger<ExternalConfigurationStartupValidator>();

                                return new ExternalConfigurationStartupValidatorService(validator, originalConfig, environment, logger);
                            }
                        }
                    }

                    return NullHostedService.Instance;
                });

                // Wire this up early so that any early worker logs are guaranteed to be flushed if any other
                // IHostedService has a slow startup.
                services.AddSingleton<IHostedService, WorkerConsoleLogService>();
            });

            builder.ConfigureWebJobs((context, webJobsBuilder) =>
            {
                // Built in binding registrations
                webJobsBuilder.AddExecutionContextBinding(o =>
                {
                    o.AppDirectory = applicationHostOptions.ScriptPath;
                })
                .AddHttp()
                .AddTimersWithStorage()
                .AddManualTrigger()
                .AddWarmup();

                var bundleManager = context.Properties.GetAndRemove<IExtensionBundleManager>(BundleManagerKey);
                webJobsBuilder.Services.AddSingleton<IExtensionBundleManager>(_ => bundleManager);

                var channelManager = context.Properties.GetAndRemove<IWebHostRpcWorkerChannelManager>(ChannelManager);
                webJobsBuilder.Services.AddSingleton<IWebHostRpcWorkerChannelManager>(_ => channelManager);

                if (!skipHostInitialization)
                {
                    var webJobsBuilderContext = new WebJobsBuilderContext
                    {
                        Configuration = context.Configuration,
                        EnvironmentName = context.HostingEnvironment.EnvironmentName,
                        ApplicationRootPath = applicationHostOptions.ScriptPath
                    };

                    // Only set our external startup if we're not suppressing host initialization
                    // as we don't want to load user assemblies otherwise.
                    var locator = context.Properties.GetAndRemove<ScriptStartupTypeLocator>(StartupTypeLocatorKey);

                    try
                    {
                        webJobsBuilder.UseExternalStartup(locator, webJobsBuilderContext, loggerFactory);
                    }
                    catch (Exception ex)
                    {
                        string appInsightsConnStr = context.Configuration[EnvironmentSettingNames.AppInsightsConnectionString];
                        RecordAndThrowExternalStartupException("Error configuring services in an external startup class.", ex, loggerFactory, appInsightsConnStr);
                    }
                }

                configureWebJobs?.Invoke(webJobsBuilder);
            }, o => o.AllowPartialHostStartup = true,
            (context, webJobsConfigBuilder) =>
            {
                if (!skipHostInitialization)
                {
                    var webJobsBuilderContext = new WebJobsBuilderContext
                    {
                        Configuration = context.Configuration,
                        EnvironmentName = context.HostingEnvironment.EnvironmentName,
                        ApplicationRootPath = applicationHostOptions.ScriptPath
                    };

                    // Delay this call so we can call the customer's setup last.
                    context.Properties[DelayedConfigurationActionKey] = new Action<IWebJobsStartupTypeLocator>(locator =>
                    {
                        try
                        {
                            webJobsConfigBuilder.UseExternalConfigurationStartup(locator, webJobsBuilderContext, loggerFactory);
                        }
                        catch (Exception ex)
                        {
                            // Go directly to the environment; We have no valid configuration from the customer at this point.
                            string appInsightsConnStr = Environment.GetEnvironmentVariable(EnvironmentSettingNames.AppInsightsConnectionString);
                            RecordAndThrowExternalStartupException("Error building configuration in an external startup class.", ex, loggerFactory, appInsightsConnStr);
                        }
                    });
                }
            });

            // Script host services - these services are scoped to a host instance, and when a new host
            // is created, these services are recreated
            builder.ConfigureServices(services =>
            {
                // Core WebJobs/Script Host services
                services.AddSingleton<ScriptHost>();

                // HTTP Worker
                services.AddSingleton<IHttpWorkerProcessFactory, HttpWorkerProcessFactory>();
                services.AddSingleton<IHttpWorkerChannelFactory, HttpWorkerChannelFactory>();
                services.AddSingleton<IHttpWorkerService, DefaultHttpWorkerService>();
                // Rpc Worker
                services.AddSingleton<IJobHostRpcWorkerChannelManager, JobHostRpcWorkerChannelManager>();
                services.AddSingleton<IRpcFunctionInvocationDispatcherLoadBalancer, RpcFunctionInvocationDispatcherLoadBalancer>();

                //Worker Function Invocation dispatcher
                services.AddSingleton<IFunctionInvocationDispatcherFactory, FunctionInvocationDispatcherFactory>();
                services.AddSingleton<IScriptJobHost>(p => p.GetRequiredService<ScriptHost>());
                services.AddSingleton<IJobHost>(p => p.GetRequiredService<ScriptHost>());
                services.AddSingleton<IFunctionProvider, ProxyFunctionProvider>();
                services.AddSingleton<IHostedService, WorkerConcurrencyManager>();

                services.AddSingleton<ITypeLocator, ScriptTypeLocator>();
                services.AddSingleton<ScriptSettingsManager>();
                services.AddTransient<IExtensionsManager, ExtensionsManager>();
                services.TryAddSingleton<IHttpRoutesManager, DefaultHttpRouteManager>();
                services.TryAddSingleton<IMetricsLogger, MetricsLogger>();
                services.AddTransient<IExtensionBundleContentProvider, ExtensionBundleContentProvider>();

                // Script binding providers
                services.TryAddEnumerable(ServiceDescriptor.Singleton<IScriptBindingProvider, WebJobsCoreScriptBindingProvider>());
                services.TryAddEnumerable(ServiceDescriptor.Singleton<IScriptBindingProvider, CoreExtensionsScriptBindingProvider>());
                services.TryAddEnumerable(ServiceDescriptor.Singleton<IScriptBindingProvider, GeneralScriptBindingProvider>());

                // Configuration
                services.AddSingleton<IOptions<ScriptApplicationHostOptions>>(new OptionsWrapper<ScriptApplicationHostOptions>(applicationHostOptions));
                services.AddSingleton<IOptionsMonitor<ScriptApplicationHostOptions>>(new ScriptApplicationHostOptionsMonitor(applicationHostOptions));
                services.ConfigureOptions<ScriptJobHostOptionsSetup>();
                services.ConfigureOptions<JobHostFunctionTimeoutOptionsSetup>();
                // LanguageWorkerOptionsSetup should be registered in WebHostServiceCollection as well to enable starting worker processing in placeholder mode.
                services.ConfigureOptions<LanguageWorkerOptionsSetup>();
                services.AddOptions<WorkerConcurrencyOptions>();
                services.ConfigureOptions<HttpWorkerOptionsSetup>();
                services.ConfigureOptions<ManagedDependencyOptionsSetup>();
                services.AddOptions<FunctionResultAggregatorOptions>()
                    .Configure<IConfiguration>((o, c) =>
                    {
                        c.GetSection(ConfigurationSectionNames.JobHost)
                         .GetSection(ConfigurationSectionNames.Aggregator)
                         .Bind(o);
                    });
                services.ConfigureOptions<ScaleOptionsSetup>();

                services.AddSingleton<IFileLoggingStatusManager, FileLoggingStatusManager>();

                if (!applicationHostOptions.HasParentScope)
                {
                    AddCommonServices(services);
                }

                if (SystemEnvironment.Instance.IsKubernetesManagedHosting())
                {
                    services.AddSingleton<IDistributedLockManager, KubernetesDistributedLockManager>();
                }

                services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, FunctionInvocationDispatcherShutdownManager>());

                services.AddSingleton<IHostOptionsProvider, HostOptionsProvider>();
            });

            RegisterFileProvisioningService(builder);
            return builder;
        }

        public static void AddCommonServices(IServiceCollection services)
        {
            // The scope for these services is beyond a single host instance.
            // They are not recreated for each new host instance, so you have
            // to be careful with caching, etc. E.g. these services will get
            // initially created in placeholder mode and live on through the
            // specialized app.
            services.AddSingleton<HostNameProvider>();
            services.AddSingleton<HostIdValidator>();
            services.AddSingleton<IHostIdProvider, ScriptHostIdProvider>();
            services.TryAddSingleton<IScriptEventManager, ScriptEventManager>();
            services.AddSingleton<IWorkerProfileManager, WorkerProfileManager>();

            // Add Language Worker Service
            // Need to maintain the order: Add RpcInitializationService before core script host services
            services.AddManagedHostedService<RpcInitializationService>();
            services.TryAddSingleton<IWorkerConsoleLogSource, WorkerConsoleLogSource>();
            services.AddSingleton<IWorkerProcessFactory, DefaultWorkerProcessFactory>();
            services.AddSingleton<IRpcWorkerProcessFactory, RpcWorkerProcessFactory>();
            services.TryAddSingleton<IDebugManager, DebugManager>();
            services.TryAddSingleton<IDebugStateProvider, DebugStateProvider>();
            services.TryAddSingleton<IEnvironment>(SystemEnvironment.Instance);
            services.TryAddSingleton<HostPerformanceManager>();
            services.ConfigureOptions<HostHealthMonitorOptionsSetup>();

            AddProcessRegistry(services);
        }

        public static IHostBuilder SetAzureFunctionsEnvironment(this IHostBuilder builder)
        {
            if (builder == null)
            {
                throw new ArgumentNullException(nameof(builder));
            }

            string azureFunctionsEnvironment = Environment.GetEnvironmentVariable(EnvironmentSettingNames.EnvironmentNameKey);

            if (azureFunctionsEnvironment != null)
            {
                builder.UseEnvironment(azureFunctionsEnvironment);
            }

            return builder;
        }

        public static IHostBuilder SetAzureFunctionsConfigurationRoot(this IHostBuilder builder)
        {
            builder.ConfigureAppConfiguration(c =>
            {
                c.AddInMemoryCollection(new Dictionary<string, string>
                    {
                        { "AzureWebJobsConfigurationSection", ConfigurationSectionNames.JobHost }
                    });
            });

            return builder;
        }

        internal static void ConfigureApplicationInsights(HostBuilderContext context, ILoggingBuilder builder)
        {
            string appInsightsInstrumentationKey = context.Configuration[EnvironmentSettingNames.AppInsightsInstrumentationKey];
            string appInsightsConnectionString = context.Configuration[EnvironmentSettingNames.AppInsightsConnectionString];

            // Initializing AppInsights services during placeholder mode as well to avoid the cost of JITting these objects during specialization
            if (!string.IsNullOrEmpty(appInsightsInstrumentationKey) || !string.IsNullOrEmpty(appInsightsConnectionString) || SystemEnvironment.Instance.IsPlaceholderModeEnabled())
            {
                builder.AddApplicationInsightsWebJobs(o =>
                {
                    o.InstrumentationKey = appInsightsInstrumentationKey;
                    o.ConnectionString = appInsightsConnectionString;
                }, t =>
                {
                    if (t.TelemetryChannel is ServerTelemetryChannel channel)
                    {
                        channel.TransmissionStatusEvent += TransmissionStatusHandler.Handler;
                    }

                    t.TelemetryProcessorChainBuilder.Use(next => new WorkerTraceFilterTelemetryProcessor(next));
                    t.TelemetryProcessorChainBuilder.Use(next => new ScriptTelemetryProcessor(next));
                });

                builder.Services.ConfigureOptions<ApplicationInsightsLoggerOptionsSetup>();
                builder.Services.AddSingleton<ISdkVersionProvider, FunctionsSdkVersionProvider>();
                builder.Services.AddSingleton<ITelemetryInitializer, ScriptTelemetryInitializer>();

                if (SystemEnvironment.Instance.IsPlaceholderModeEnabled())
                {
                    // Disable auto-http and dependency tracking when in placeholder mode.
                    builder.Services.Configure<ApplicationInsightsLoggerOptions>(o =>
                    {
                        o.HttpAutoCollectionOptions.EnableHttpTriggerExtendedInfoCollection = false;
                        o.EnableDependencyTracking = false;
                    });
                }
            }
        }

        internal static ExtensionBundleOptions GetExtensionBundleOptions(IConfiguration configuration)
        {
            var options = new ExtensionBundleOptions();
            var optionsSetup = new ExtensionBundleConfigurationHelper(configuration, SystemEnvironment.Instance);
            configuration.Bind(options);
            optionsSetup.Configure(options);
            return options;
        }

        private static void RegisterFileProvisioningService(IHostBuilder builder)
        {
            if (string.Equals(Environment.GetEnvironmentVariable(RpcWorkerConstants.FunctionWorkerRuntimeSettingName), "powershell"))
            {
                builder.ConfigureServices(services =>
                {
                    services.AddSingleton<IFuncAppFileProvisionerFactory, FuncAppFileProvisionerFactory>();
                    services.AddSingleton<IHostedService, FuncAppFileProvisioningService>();
                });
            }
        }

        private static void AddProcessRegistry(IServiceCollection services)
        {
            // W3WP already manages job objects
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                && !ScriptSettingsManager.Instance.IsAppServiceEnvironment)
            {
                services.AddSingleton<IProcessRegistry, JobObjectRegistry>();
            }
            else
            {
                services.AddSingleton<IProcessRegistry, EmptyProcessRegistry>();
            }
        }

        /// <summary>
        /// Gets and removes the specified value, if it exists and is of type T.
        /// Throws an InvalidOperationException if the key does not exist or is not of type T.
        /// </summary>
        /// <typeparam name="T">The expected type of the value.</typeparam>
        /// <param name="dictionary">The dictinoary.</param>
        /// <param name="key">They key to remove and return.</param>
        /// <returns>The value.</returns>
        private static T GetAndRemove<T>(this IDictionary<object, object> dictionary, string key) where T : class
        {
            if (dictionary.TryGetValue(key, out object valueObj))
            {
                if (valueObj is T value)
                {
                    dictionary.Remove(key);
                    return value;
                }
                else
                {
                    throw new InvalidOperationException($"The key '{key}' exists in the dictionary but is type '{valueObj.GetType()}' instead of type '{typeof(T)}'.");
                }
            }
            else
            {
                throw new InvalidOperationException($"The key '{key}' does not exist in the dictionary.");
            }
        }

        private static void RecordAndThrowExternalStartupException(string message, Exception ex, ILoggerFactory loggerFactory, string appInsightsConnStr)
        {
            var startupEx = new ExternalStartupException(message, ex);

            var logger = loggerFactory.CreateLogger(LogCategories.Startup);
            logger.LogDiagnosticEventError(DiagnosticEventConstants.ExternalStartupErrorCode, message, DiagnosticEventConstants.ExternalStartupErrorHelpLink, startupEx);

            // Send the error to App Insights if possible. This is happening during ScriptHost construction so we
            // have no existing TelemetryClient to use. Create a one-off client and flush it ASAP.
            if (appInsightsConnStr is not null)
            {
                using TelemetryConfiguration telemetryConfiguration = new()
                {
                    ConnectionString = appInsightsConnStr,
                    TelemetryChannel = new InMemoryChannel()
                };

                TelemetryClient telemetryClient = new(telemetryConfiguration);
                telemetryClient.Context.GetInternalContext().SdkVersion = new ApplicationInsightsSdkVersionProvider().GetSdkVersion();
                telemetryClient.TrackTrace(startupEx.ToString(), SeverityLevel.Error);
                telemetryClient.TrackException(startupEx);
                telemetryClient.Flush();
            }

            throw startupEx;
        }
    }
}