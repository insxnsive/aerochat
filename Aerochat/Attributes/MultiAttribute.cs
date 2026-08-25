namespace Aerochat.Attributes
{
    [AttributeUsage(AttributeTargets.Property, AllowMultiple = false)]
    public sealed class MultiStringToIntAction : Attribute
    {
        public Func<List<string>> GetDisplayNames { get; } = static () => [];
        public MultiStringToIntAction(string action) { }
    }
}
