﻿// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the MIT License. See License.txt in the project root for license information.

namespace WorkerHarness.Core.WorkerProcess
{
    /// <summary>
    /// an abstraction of a worker process
    /// </summary>
    public interface IWorkerProcess
    {
        int Id { get; }
        bool HasExited { get; }
        bool Start();
        void WaitForProcessExit(int milliseconds);
        void Dispose();
    }
}
