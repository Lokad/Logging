using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Reflection.Emit;
using System.Text.RegularExpressions;
using NLog;
using NLog.Layouts;
using NLog.Targets;

namespace Lokad.Logging
{
    /// <summary> Used to create instances implementing <see cref="ITrace"/> </summary>
    /// <see cref="Bind{T}(Expression{Func{T}})"/>
    [DebuggerStepThrough]
    public static class Tracer
    {
        private static readonly Dictionary<Type, object> Implementations = new Dictionary<Type, object>();

        /// <summary> The number of interface implementations created so far. </summary>
        private static int _implCount;

        /// <summary> Returns the logger for the specified class. </summary>
        private static Func<string, Logger> _getLogger;

        /// <see cref="ModuleBuilder"/>
        private static ModuleBuilder _moduleBuilderCache;

        /// <summary> Module that hosts dynamic assembly code. </summary>
        private static ModuleBuilder ModuleBuilder
        {
            get
            {
                if (_moduleBuilderCache != null) return _moduleBuilderCache;

                var assemblyBuilder = AssemblyBuilder.DefineDynamicAssembly(
                    new AssemblyName { Name = "Trace" },
                    AssemblyBuilderAccess.Run);

                return _moduleBuilderCache = assemblyBuilder.DefineDynamicModule("TraceModule");
            }
        }

        /// <summary>
        ///     Return an implementation of an interface that extends <see cref="ITrace"/>.
        ///     Uses the name of the class that contains the static field as the logger name.
        /// </summary>
        public static T Bind<T>(Expression<Func<T>> staticField)
            where T : class, ITrace
        {
            if (!typeof(T).IsInterface)
                throw new ArgumentException($"Type {typeof(T)} should be an interface.", nameof(T));

            var exp = staticField.Body;
            if (exp.NodeType == ExpressionType.Quote)
                exp = ((UnaryExpression)exp).Operand;

            if (exp.NodeType != ExpressionType.MemberAccess)
                throw new ArgumentException($"Unrecognized lambda body '{exp}'.", nameof(staticField));

            var declaringType = ((MemberExpression)exp).Member.DeclaringType
                                ?? throw new ArgumentException($"'{exp}': member has no declaring type.", nameof(staticField));

            return Bind<T>(declaringType.FullName);
        }

        /// <summary>
        ///     Return an implementation of an interface that extends <see cref="ITrace"/>.
        ///     Uses <paramref name="loggerName"/> as the logger name.
        /// </summary>
        public static T Bind<T>(string loggerName)
        {
            var impl = CreateImplementation(typeof(T), loggerName);
            return (T)impl;
        }

        /// <summary>
        /// Setup NLog to send json messages to the given Uri.
        ///
        /// The setup adds
        /// <see href="https://github.com/NLog/NLog/wiki/Network-target">NLog Network target</see>
        /// named RemoteJsonTarget using
        /// a <see href="https://github.com/NLog/NLog/wiki/JsonLayout">Json layout</see>.
        /// The messages at a log level equal or greater to the given minimal level will be sent
        /// to the given remote host as Json payloads.
        ///
        /// </summary>
        /// <param name="address">Remote server recipient of the log messages address layout.</param>
        /// <param name="apiKey">Api key appended to the log messages to get the messages accepted by the remote host.</param>
        /// <param name="application">Name of the application that will be sending the logs through the LogManager.</param>
        /// <param name="environment">Runtime Environment of the logging application.</param>
        /// <param name="minLevel">Minimal log level of the messages that should be sent.</param>
        public static void SetupRemoteJsonLogger(string address, string apiKey,
            string application, string environment, LogLevel minLevel = LogLevel.Info)
        {
            var configuration = LogManager.Configuration;

            var remoteJsonTarget = new NetworkTarget("RemoteJsonTarget")
            {
                Address = address,
                Layout = new JsonLayout()
                {
                    Attributes =
                    {
                        new JsonAttribute("Timestamp", "${longdate}"),
                        new JsonAttribute("ApiKey", apiKey),
                        new JsonAttribute("Application", application),
                        new JsonAttribute("Environment", environment),
                        new JsonAttribute("Hostname", "${machinename}"),
                        new JsonAttribute("LoggerName", "${logger}"),
                        new JsonAttribute("Message", "${message}"),
                        new JsonAttribute("Exception", "${exception}")
                    },
                    IncludeAllProperties = true
                }
            };

            configuration.AddTarget(remoteJsonTarget);
            configuration.AddRule(Level(minLevel), NLog.LogLevel.Fatal, remoteJsonTarget);

            LogManager.Configuration = configuration;
        }

        /// <summary> Implement the <see cref="ITrace"/>-derived interface. </summary>
        private static object CreateImplementation(Type interfaceType, string fullOwnerName)
        {
            if (!interfaceType.IsInterface)
                throw new ArgumentException($"{interfaceType} is not an interface type", nameof(interfaceType));

            if (!typeof(ITrace).IsAssignableFrom(interfaceType))
                throw new ArgumentException($"{interfaceType} must implement ITrace", nameof(interfaceType));

            lock (Implementations)
            {
                if (Implementations.TryGetValue(interfaceType, out var found))
                    return found;

                var module = ModuleBuilder;
                var tb = module.DefineType(
                    interfaceType.FullName + "Impl" + _implCount++,
                    TypeAttributes.Public | TypeAttributes.BeforeFieldInit,
                    typeof(BaseTrace),
                    new[] { interfaceType });

                CreateConstructor(tb);

                // override all methods (which are Logs or Activities)
                foreach (var mi in interfaceType.GetMethods())
                {
                    if (mi.IsSpecialName) continue; // skip getters/setters

                    // define the method
                    CreateOverride(tb, mi);
                }

                var type = tb.CreateTypeInfo();

                // Create the instance
                var ctor = type.GetConstructor(new[] { typeof(string) })
                        ?? throw new Exception("Expected new(string) constructor");

                found = ctor.Invoke(new object[] { fullOwnerName });

                Implementations.Add(interfaceType, found);
                return found;
            }
        }

        /// <summary> Generate a constructor for the specified type. </summary>
        /// <remarks> Constructor takes a string (the fullOwnerName), passed to base class. </remarks>
        private static void CreateConstructor(TypeBuilder tb)
        {
            var cb = tb.DefineConstructor(
                MethodAttributes.Public,
                CallingConventions.Standard,
                new[] { typeof(string) });

            var cbil = cb.GetILGenerator();

            var baseCtor = typeof(BaseTrace).GetConstructor(new[] { typeof(string) })
                        ?? throw new Exception("Expected new BaseTrace(string)");

            cbil.Emit(OpCodes.Ldarg_0); // this
            cbil.Emit(OpCodes.Ldarg_1); // fullOwnerName
            cbil.Emit(OpCodes.Call, baseCtor);
            cbil.Emit(OpCodes.Ret);
        }

        /// <summary> Create an implementation of the specified method from the interface. </summary>
        private static void CreateOverride(TypeBuilder tb, MethodInfo mi)
        {
            var mb = tb.DefineMethod(
                mi.Name,
                MethodAttributes.Public
                    | MethodAttributes.HideBySig
                    | MethodAttributes.NewSlot
                    | MethodAttributes.Virtual
                    | MethodAttributes.Final,
                CallingConventions.HasThis,
                mi.ReturnType,
                mi.GetParameters().Select(p => p.ParameterType).ToArray());

            tb.DefineMethodOverride(mb, mi);

            ImplementLogMethod(mi, mb);
        }

        /// <summary> Initializes the trace manager with a custom logger. </summary>
        /// <remarks> Should be called before the first call to an interface. </remarks>
        public static void Init(Func<string, Logger> getLogger) =>
            _getLogger = getLogger;

        /// <summary> Disabled logging altogether. </summary>
        /// <remarks> Should be called before the first call to an interface. </remarks>
        public static void Disable() =>
            _getLogger = n => LogManager.CreateNullLogger();

        /// <summary>
        /// Returns the lazily evaluated call of <see cref="_getLogger"/> on
        /// the provided name. This way, the lazy call will still  evaluate properly,
        /// even if <see cref="CreateLoggerFor"/> was created before <see cref="Init"/>
        /// or <see cref="Disable"/>.
        /// </summary>
        internal static Lazy<Logger> CreateLoggerFor(string name) =>
            new Lazy<Logger>(() => (_getLogger ?? LogManager.GetLogger)(name));

        /// <summary> Implement a logging method. </summary>
        private static void ImplementLogMethod(MethodInfo mi, MethodBuilder mbs)
        {
            var interfaceType = mi.DeclaringType;
            var names = mi.GetParameters().Select(p => p.Name).ToArray();

            // get text to log
            var attr = mi.GetCustomAttributes(typeof(ErrorAttribute), false).Cast<TraceAttribute>().FirstOrDefault()
                ?? mi.GetCustomAttributes(typeof(WarningAttribute), false).Cast<TraceAttribute>().FirstOrDefault()
                ?? mi.GetCustomAttributes(typeof(InfoAttribute), false).Cast<TraceAttribute>().FirstOrDefault()
                ?? mi.GetCustomAttributes(typeof(DebugAttribute), false).Cast<TraceAttribute>().FirstOrDefault()
                ?? mi.GetCustomAttributes(typeof(IgnoreLogAttribute), false).Cast<TraceAttribute>().FirstOrDefault()
                ?? throw new ArgumentException($"Method {interfaceType}.{mi.Name} must have log level attribute");

            var parameters = mi.GetParameters().ToArray();

            // determine if an exception is present (pick first one)
            var exn = -1;
            if (mi.ReturnType == typeof(void)) // only for 'message' methods
                for (var i = 0; i < parameters.Length && exn < 0; ++i)
                    if (typeof(Exception).IsAssignableFrom(parameters[i].ParameterType))
                        exn = i;

            // create the logged string
            var logged = attr.Message;
            for (var i = 0; i < names.Length; i++)
                logged = logged.Replace("{" + names[i] + "}", "{" + i.ToString(CultureInfo.InvariantCulture) + "}");

            var m = Regex.Match(logged, @"\{[^0-9]*\}|\{(?![0-9])|(?<![0-9])\}");
            if (m.Success)
                throw new ArgumentException($"Invalid format argument '{m.Value}' for method {interfaceType}.{mi.Name}");

            var il = mbs.GetILGenerator();

            // emit this
            il.Emit(OpCodes.Ldarg_0);

            // emit exception (if any)
            if (exn >= 0)
                il.Emit(OpCodes.Ldarg, exn + 1);

            // emit string
            il.Emit(OpCodes.Ldstr, logged);

            if (names.Length > 0)
            {
                // create array
                il.Emit(OpCodes.Ldc_I4, names.Length);
                il.Emit(OpCodes.Newarr, typeof(object));

                // fill array
                for (var i = 0; i < names.Length; i++)
                {
                    // The parameter value
                    il.Emit(OpCodes.Dup);
                    il.Emit(OpCodes.Ldc_I4, i);
                    il.Emit(OpCodes.Ldarg, i + 1);
                    if (parameters[i].ParameterType.IsValueType)
                        il.Emit(OpCodes.Box, parameters[i].ParameterType);
                    il.Emit(OpCodes.Stelem_Ref);
                }

                // call string.format
                il.Emit(OpCodes.Call, typeof(string).GetMethod("Format", new[] { typeof(string), typeof(object[]) })
                                      ?? throw new Exception("Could not find string.Format(string, object[])."));
            }

            // Emit dictionary of keys
            var kept = Enumerable.Range(0, parameters.Length)
                .Where(p => parameters[p].ParameterType == typeof(string) || parameters[p].ParameterType.IsPrimitive)
                .ToArray();

            if (kept.Length == 0)
            {
                il.Emit(OpCodes.Ldnull);
            }
            else
            {
                il.Emit(OpCodes.Ldc_I4, kept.Length * 2);
                il.Emit(OpCodes.Newarr, typeof(object));

                for (var i = 0; i < kept.Length; ++i)
                {
                    // The parameter name
                    il.Emit(OpCodes.Dup);
                    il.Emit(OpCodes.Ldc_I4, 2 * i);
                    il.Emit(OpCodes.Ldstr, parameters[kept[i]].Name);
                    il.Emit(OpCodes.Stelem_Ref);

                    // The argument value
                    il.Emit(OpCodes.Dup);
                    il.Emit(OpCodes.Ldc_I4, 2 * i + 1);
                    il.Emit(OpCodes.Ldarg, kept[i] + 1);
                    if (parameters[kept[i]].ParameterType.IsValueType)
                        il.Emit(OpCodes.Box, parameters[kept[i]].ParameterType);
                    il.Emit(OpCodes.Stelem_Ref);
                }

                il.Emit(OpCodes.Call, typeof(Tracer).GetMethod(nameof(MakeContext), new[] { typeof(object[]) })
                                      ?? throw new Exception("Could not find Trace.MakeContext(object[])"));
            }

            // emit log level
            il.Emit(OpCodes.Ldc_I4, (int)attr.Level);

            // perform call 
            if (mi.ReturnType == typeof(Activity))
            {
                var ctor = typeof(Activity).GetConstructor(new[]
                {
                    typeof (BaseTrace),
                    typeof (string),
                    typeof (Dictionary<string, object>),
                    typeof (LogLevel)
                }) ?? throw new Exception("No constructor found for Activity");

                il.Emit(OpCodes.Newobj, ctor);
            }
            else if (mi.ReturnType == typeof(void))
            {
                il.Emit(OpCodes.Call,
                    typeof(Tracer).GetMethod(exn < 0 ? nameof(Emit) : nameof(EmitWithException))
                    ?? throw new Exception($"Could not find Tracer.{(exn < 0 ? nameof(Emit) : nameof(EmitWithException))}()"));
            }
            else
            {
                throw new ArgumentException(
                    $"Return type {mi.ReturnType} not supported, expected void or Activity",
                    nameof(mi));
            }

            il.Emit(OpCodes.Ret);
        }

        /// <summary> Print a message on the provided tracer. </summary>
        /// <remarks> Used internally by the compiled interfaces. </remarks>
        public static void Emit(
            BaseTrace bt,
            string message,
            Dictionary<string, object> ctx,
            LogLevel level)
        =>
            bt.EmitLogMessage(message, ctx, level);

        /// <summary> Print a message and exception on the provided tracer. </summary>
        /// <remarks> Used internally by the compiled interfaces. </remarks>
        public static void EmitWithException(
            BaseTrace bt,
            Exception ex,
            string message,
            Dictionary<string, object> ctx,
            LogLevel level)
        =>
            bt.EmitLogMessage(ex, message, ctx, level);

        /// <summary> Construct a dictionary from a context (as [key,value,key,value] array). </summary>
        public static IReadOnlyDictionary<string, object> MakeContext(IReadOnlyList<object> p)
        {
            var d = new Dictionary<string, object>();
            for (var i = 0; i < p.Count; i += 2)
            {
                var key = (string)p[i];
                if (d.ContainsKey(key)) continue;
                var val = p[i + 1];
                d.Add(key, val);
            }
            return d;
        }

        /// <summary> Translate the log-level enumeration to an NLog.LogLevel </summary>
        public static NLog.LogLevel Level(LogLevel level)
        {
            switch (level)
            {
                case LogLevel.Debug: return NLog.LogLevel.Debug;
                case LogLevel.Error: return NLog.LogLevel.Error;
                case LogLevel.Info: return NLog.LogLevel.Info;
                case LogLevel.Warning: return NLog.LogLevel.Warn;
                case LogLevel.None: return NLog.LogLevel.Off;
                default:
                    throw new ArgumentException("Unknown log level: " + level, nameof(level));
            }
        }
    }
}
