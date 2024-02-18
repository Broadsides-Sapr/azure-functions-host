﻿// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the MIT License. See License.txt in the project root for license information.

using Channels = System.Threading.Channels;
using Microsoft.Azure.Functions.WorkerHarness.Grpc.Messages;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using WorkerHarness.Core;
using WorkerHarness.Core.WorkerProcess;
using WorkerHarness.Core.Options;
using WorkerHarness.Core.Variables;
using WorkerHarness.Core.Matching;
using WorkerHarness.Core.Validators;
using WorkerHarness.Core.Parsing;
using WorkerHarness.Core.GrpcService;
using WorkerHarness.Core.StreamingMessageService;
using WorkerHarness.Core.Actions;
using System.Reflection;
using WorkerHarness.Core.Diagnostics;

namespace WorkerHarness
{
    public class Program
    {
        public static async Task Main(string[] args)
        {
            HarnessEventSource.Log.AppStarted();
            ServiceProvider? serviceProvider = null;
            IGrpcServer? grpcServer = null;
            try
            {
                Console.WriteLine($"Worker Harness version: {GetHarnessVersion()}");

                if (!TryGetHarnessSetting(out string harnessSettingsPath))
                {
                    return;
                }

                serviceProvider = SetupDependencyInjection(harnessSettingsPath);

                // validate user input
                IOptions<HarnessOptions> harnessOptions = serviceProvider.GetRequiredService<IOptions<HarnessOptions>>()!;

                IHarnessOptionsValidate harnessValidate = serviceProvider.GetRequiredService<IHarnessOptionsValidate>();

                if (!harnessValidate.Validate(harnessOptions.Value))
                {
                    serviceProvider.Dispose();
                    return;
                }

                // start the grpc server
                grpcServer = serviceProvider.GetRequiredService<IGrpcServer>();
                grpcServer.Start();

                // run the harness
                var harnessExecutor = serviceProvider.GetRequiredService<IWorkerHarnessExecutor>();
                await harnessExecutor.StartAsync();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"An exception occurred: {ex.Message}");
            }
            finally
            {
                Console.WriteLine($"Exiting...");
                if (grpcServer is not null)
                {
                    await grpcServer.Shutdown();
                }
                serviceProvider?.Dispose();
            }
        }

        private static ServiceProvider SetupDependencyInjection(string harnessSettingsPath)
        {
            IConfigurationBuilder configurationBuilder = new ConfigurationBuilder()
                .AddJsonFile(harnessSettingsPath);
            IConfiguration config = configurationBuilder.Build();

            ServiceProvider serviceProvider = new ServiceCollection()
                .AddSingleton<IWorkerProcessBuilder, SystemProcessBuilder>()
                .AddSingleton<IScenarioParser, ScenarioParser>()
                .AddSingleton<IStreamingMessageProvider, StreamingMessageProvider>()
                .AddSingleton<IPayloadVariableSolver, PayloadVariableSolver>()
                .AddSingleton<IValidatorFactory, ValidatorFactory>()
                .AddSingleton<IVariableObservable, VariableManager>()
                .AddSingleton<IMessageMatcher, MessageMatcher>()
                .AddSingleton<IContextMatcher, ContextMatcher>()
                .AddSingleton<IActionProvider, RpcActionProvider>()
                .AddSingleton<IActionProvider, DelayActionProvider>()
                .AddSingleton<IActionProvider, ImportActionProvider>()
                .AddSingleton<IActionProvider, TerminateActionProvider>()
                .AddSingleton<IWorkerHarnessExecutor, WorkerHarnessExecutor>()
                .AddSingleton<GrpcServiceChannel>(s =>
                {
                    Channels.UnboundedChannelOptions outputOptions = new()
                    {
                        SingleWriter = false,
                        SingleReader = true,
                        AllowSynchronousContinuations = true
                    };

                    return new GrpcServiceChannel(Channels.Channel.CreateUnbounded<StreamingMessage>(outputOptions),
                        Channels.Channel.CreateUnbounded<StreamingMessage>(outputOptions));
                })
                .AddSingleton<IHarnessOptionsValidate, HarnessOptionsValidate>()
                .AddSingleton<IGrpcServer, GrpcServer>()
                .Configure<HarnessOptions>(config)
                .AddLogging(c => { c.AddConsole(); })
                .BuildServiceProvider();

            return serviceProvider;
        }

        private static bool TryGetHarnessSetting(out string harnessSettingPath)
        {
            string MissingHarnessSettingJsonFile = "Missing the required harness.settings.json file in the current directory.";

            harnessSettingPath = Path.Combine(Directory.GetCurrentDirectory(), "harness.settings.json");

            if (!File.Exists(harnessSettingPath))
            {
                Console.WriteLine(MissingHarnessSettingJsonFile);
                Console.WriteLine(harnessSettingPath);
                return false;
            }

            return true;
        }

        private static string GetHarnessVersion()
        {
            const string version = Constants.WorkerHarnessVersion;
            if(!string.IsNullOrWhiteSpace(version))
            {
                return version;
            }
            // fall back to reflection.
            return Assembly.GetAssembly(typeof(Program))!.GetName().Version!.ToString();
        }
    }
}
