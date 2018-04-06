A logging framework. We really had fun reinventing this particular wheel.

To use `Lokad.Logging`, declare a trace interface such as:

```
interface IMyTrace : ITrace
{
	[Debug("This is how I print my arguments : {argA} to a string {b}")]
	void MyEventThatMustBeLogged(string argA, int b);

	[Info("This is a timer named {abc} that must be used in a using() statement")]
	Activity StartMyActivityWithTimer(string abc);

	[Warn("In situation {s}, an exception was thrown (and will be logged)")]
	void AnExceptionWasThrown(Exception ex, string s);

	[Error("This is the highest level of log info.")]
	void WithoutArguments();
 }
```

Declare a static property in the class for which you want to do logging:

```
public class MyClass 
{
    static readonly IMyTrace Log = Tracer.Bind<IMyTrace>("MyClass");
}
```

The bind function will use reflection and code generation to create an implementation 
of `IMyTrace` that pushes any log lines to NLog (and from there, to the console, to
log files, or to somewhere else). The provided argument will be passed to the 
constructor of the underlying `NLog.Logger`.

A shorter variant uses reflection to deduce both arguments:

```
public class MyClass 
{
    static readonly IMyTrace Log = Tracer.Bind(() => Log);
}
```

In this variant, the function passed to `Bind` should be a simple lambda that returns 
the static field itself. 

To emit a message, simply call the corresponding method on the interface: 

```
Log.MyEventThatMustBeLogged("my argument", 12);  
```

When a method returns an `Activity`, it will generate two log messages, one when the 
method is called and another when the `Activity` is disposed (and this second 
message also includes the duration between the two), so make sure you dispose it in 
order to trigger the emission of the end message. 

```
using(Log.StartMyActivityWithTimer("hello"))
{
	// Do expensive computations. 
	// Time taken will be logged on activity disposal.
}
```

Only `string`, `bool` and primitive number types are allowed as logging method 
arguments. Additionally, a single `Exception` argument can be provided, in which 
case the stack trace will be passed in field `NLog.LogEventInfo.Exception`.