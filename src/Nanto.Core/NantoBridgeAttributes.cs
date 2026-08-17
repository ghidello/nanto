namespace Nanto;

[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class NantoApiAttribute : Attribute;

[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class NantoApiPartAttribute<TApi> : Attribute;

[AttributeUsage(AttributeTargets.Method, Inherited = false)]
public sealed class NantoCommandAttribute : Attribute;

[AttributeUsage(AttributeTargets.Property, Inherited = false)]
public sealed class NantoEventAttribute : Attribute;
