﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using YamlDotNet.RepresentationModel;

namespace Microwalk.TestcaseGeneration
{
    /// <summary>
    /// Abstract base class for the test case generation stage.
    /// </summary>
    abstract class TestcaseStage : PipelineStage
    {
        /// <summary>
        /// Factory object for modules implementing this stage.
        /// </summary>
        public static ModuleFactory<TestcaseStage> Factory { get; } = new ModuleFactory<TestcaseStage>();

        /// <summary>
        /// Generates a new testcase and returns a fitting <see cref="TraceEntity"/> object.
        /// </summary>
        /// <param name="token">Cancellation token to stop test case generation early.</param>
        /// <returns></returns>
        public abstract Task<TraceEntity> NextTestcaseAsync(CancellationToken token);

        /// <summary>
        /// Returns whether the test case stage has completed and does not produce further inputs. This method is called before requesting a new test case.
        /// </summary>
        /// <returns></returns>
        public abstract Task<bool> IsDoneAsync();
    }
}
