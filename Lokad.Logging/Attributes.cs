using System;

namespace Lokad.Logging
{
    /// <summary> The base class for all attributes. </summary>
    /// <remarks>
    ///     The goal of an attribute is to specify the <see cref="LogLevel"/>
    ///     (this is done automatically depending on which child class is used)
    ///     and the message (which is actually a format).
    /// </remarks>
    public abstract class TraceAttribute : Attribute
    {
        public string Message { get; protected set; }
        public LogLevel Level { get; protected set; }
    }

    /// <see cref="LogLevel.Debug"/>
    public class DebugAttribute : TraceAttribute
    {
        public DebugAttribute(string info)
        {
            Message = info;
            Level = LogLevel.Debug;
        }
    }

    /// <see cref="LogLevel.Info"/>
    public class InfoAttribute : TraceAttribute
    {
        public InfoAttribute(string info)
        {
            Message = info;
            Level = LogLevel.Info;
        }

    }

    /// <see cref="LogLevel.Warning"/>
    public class WarningAttribute : TraceAttribute
    {
        public WarningAttribute(string info)
        {
            Level = LogLevel.Warning;
            Message = info;
        }
    }

    /// <see cref="LogLevel.Error"/>
    public class ErrorAttribute : TraceAttribute
    {
        public ErrorAttribute(string info)
        {
            Message = info;
            Level = LogLevel.Error;
        }
    }

    /// <summary> Does not send messages. </summary>
    public class IgnoreLogAttribute : TraceAttribute
    {
        public IgnoreLogAttribute() : this("ignored") { }

        public IgnoreLogAttribute(string ignored)
        {
            Message = ignored;
            Level = LogLevel.None;
        }
    }
}
