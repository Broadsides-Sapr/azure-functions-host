﻿using Microsoft.Azure.Functions.WorkerHarness.Grpc.Messages;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the MIT License. See License.txt in the project root for license information.

using WorkerHarness.Core.Commons;

namespace WorkerHarness.Core.Validators
{
    internal class StringValidator : IValidator
    {
        public bool Validate(ValidationContext context, object message)
        {
            try
            {
                string query = context.Query;
                string queryResult = message.Query(query);

                context.TryEvaluate(out string? expected);

                return string.Equals(queryResult, expected, StringComparison.Ordinal);
            }
            catch (ArgumentException ex)
            {
                throw ex;
            }

        }
    }
}
