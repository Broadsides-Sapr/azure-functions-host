﻿// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the MIT License. See License.txt in the project root for license information.

using Grpc.Core.Logging;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Diagnostics;

namespace WorkerHarness.Core
{
    public class DefaultWorkerHarnessExecutor : IWorkerHarnessExecutor
    {
        private readonly WorkerDescription _workerDescription;
        private readonly IWorkerProcessBuilder _workerProcessBuilder;
        private readonly IScenarioParser _scenarioParser;
        private readonly ILogger<DefaultWorkerHarnessExecutor> _logger;

        public DefaultWorkerHarnessExecutor(IOptions<WorkerDescription> workerDescription,
            IWorkerProcessBuilder workerProcessBuilder,
            IScenarioParser scenarioParser,
            ILogger<DefaultWorkerHarnessExecutor> logger)
        {
            _workerDescription = workerDescription.Value;
            _workerProcessBuilder = workerProcessBuilder;
            _scenarioParser = scenarioParser;
            _logger = logger;
        }

        public async Task<bool> Start(string scenarioFile)
        {

            Process myProcess = _workerProcessBuilder.Build(_workerDescription);

            Scenario scenario = _scenarioParser.Parse(scenarioFile);

            try
            {
                myProcess.Start();

                foreach (IAction action in scenario.Actions)
                {
                    ActionResult result = await action.ExecuteAsync();

                    ShowActionResult(result);
                    
                }

                myProcess.Kill();
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"\n\n{ex}");

                return false;
            }
        }

        private void ShowActionResult(ActionResult result)
        {
            switch (result.Status)
            {
                case StatusCode.Success:
                    _logger.LogInformation("{ActionType}: {ActionName} ... {Status}", result.ActionType, result.ActionName, result.Status);
                    break;
                case StatusCode.Error:
                    _logger.LogError("{ActionType}: {ActionName} ... {Status}", result.ActionType, result.ActionName, result.Status);
                    break;
                case StatusCode.Timeout:
                    _logger.LogError("{ActionType}: {ActionName} ... {Status}", result.ActionType, result.ActionName, result.Status);
                    break;
                default:
                    _logger.LogInformation("{ActionType}: {ActionName} ... {Status}", result.ActionType, result.ActionName, result.Status);
                    break;
            }

            foreach (string message in result.Messages)
            {
                _logger.LogInformation("{Message}", message);
            }

            // sleep for 1000 miliseconds to push all logs to the console
            Thread.Sleep(1000);
        }
    }
}
