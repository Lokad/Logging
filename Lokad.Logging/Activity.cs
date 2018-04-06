using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace Lokad.Logging
{
    /// <summary>
    ///     A timed activity, starts counting when created, emits a log message
    ///     upon creation and upon disposal (the latter including the duration).
    /// </summary>
    public sealed class Activity : IDisposable
    {
        /// <summary> The name of the activity, used as the messages sent to the log. </summary>
        private readonly string _activityName;

        /// <summary> The log level that the messages will use. </summary>
        private readonly LogLevel _activityLevel;

        /// <summary> Started when the activity is created.  </summary>
        private readonly Stopwatch _watch;

        /// <summary> Destination of the log messages. </summary>
        private readonly BaseTrace _trace;

        /// <see cref="BaseTrace.EmitLogMessage(string, IReadOnlyDictionary{string, object}, LogLevel)"/>
        private readonly IReadOnlyDictionary<string, object> _context; 
        
        public Activity(
            BaseTrace trace, 
            string activityName, 
            IReadOnlyDictionary<string, object> context, 
            LogLevel activityLevel = LogLevel.Info)
        {
            _activityName = activityName;
            _activityLevel = activityLevel;
            _context = context;
            _trace = trace;
            _watch = Stopwatch.StartNew();
            _trace.EmitLogMessage(activityName + " [+]", _context, activityLevel);
        }

        public void Dispose()
        {
            if (_trace == null) return;

            _watch.Stop();

            var elapsed = _watch.Elapsed.ToString(
                _watch.ElapsedMilliseconds < 60 * 1000 ? "s'.'fff" : "m':'ss'.'fff");

            _trace.EmitLogMessage(_activityName + " [" + elapsed + "]", _context, _activityLevel);
        }
    }
}
