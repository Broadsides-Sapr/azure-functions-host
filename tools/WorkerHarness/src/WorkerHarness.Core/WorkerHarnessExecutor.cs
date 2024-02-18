﻿// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the MIT License. See License.txt in the project root for license information.

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WorkerHarness.Core.Actions;
using WorkerHarness.Core.GrpcService;
using WorkerHarness.Core.Options;
using WorkerHarness.Core.Parsing;
using WorkerHarness.Core.Variables;
using WorkerHarness.Core.WorkerProcess;

namespace WorkerHarness.Core
{
    public class WorkerHarnessExecutor : IWorkerHarnessExecutor
    {
        private readonly IWorkerProcessBuilder _workerProcessBuilder;
        private readonly IScenarioParser _scenarioParser;
        private readonly ILogger<WorkerHarnessExecutor> _logger;
        private readonly HarnessOptions _harnessOptions;
        private readonly IVariableObservable _globalVariables;
        private readonly Uri _serverUri;

        public WorkerHarnessExecutor(
            IWorkerProcessBuilder workerProcessBuilder,
            IScenarioParser scenarioParser,
            ILogger<WorkerHarnessExecutor> logger,
            IOptions<HarnessOptions> harnessOptions,
            IVariableObservable variableObservable,
            IGrpcServer grpcServer)
        {
            _workerProcessBuilder = workerProcessBuilder;
            _scenarioParser = scenarioParser;
            _logger = logger;
            _harnessOptions = harnessOptions.Value;
            _globalVariables = variableObservable;
            _serverUri = grpcServer.Uri;
        }

        public async Task<bool> StartAsync()
        {
            WorkerContext context = new(_harnessOptions.LanguageExecutable!,
                _harnessOptions.LanguageExecutableArguments,
                _harnessOptions.WorkerPath!,
                _harnessOptions.WorkerArguments,
                _harnessOptions.FunctionAppDirectory!,
                _serverUri);

            IWorkerProcess myProcess = _workerProcessBuilder.Build(context);

            ExecutionContext executionContext = new(_globalVariables, _scenarioParser, myProcess)
            {
                DisplayVerboseError = _harnessOptions.DisplayVerboseError
            };

            try
            {
                string scenarioFile = _harnessOptions.ScenarioFile!;
                Scenario scenario = _scenarioParser.Parse(scenarioFile);

                myProcess.Start();

                _logger.LogInformation("Executing scenario: {0}", scenario.ScenarioName);

                foreach (IAction action in scenario.Actions)
                {
                    ActionResult actionResult = await action.ExecuteAsync(executionContext);

                    if (!_harnessOptions.ContinueUponFailure && actionResult.Status == StatusCode.Failure)
                    {
                        break;
                    }
                }

                return true;
            }
            catch (Exception ex)
            {
                _logger.LogCritical("An exception occurs: \n{0}\n{1}", ex.Message, ex.StackTrace);

                return false;
            }
            finally
            {
                myProcess.Dispose();
            }
        }
    }
}
