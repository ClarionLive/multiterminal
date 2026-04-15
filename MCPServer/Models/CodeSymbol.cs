namespace MultiTerminal.MCPServer.Models
{
    public class CodeSymbol
    {
        public long Id { get; set; }
        public string Name { get; set; }
        public string Type { get; set; }              // class, interface, struct, enum, method, property, constructor, field, event, delegate
        public string FilePath { get; set; }
        public int LineNumber { get; set; }
        public int ProjectId { get; set; }
        public string Params { get; set; }            // method parameter signature
        public string ReturnType { get; set; }
        public string ParentName { get; set; }        // base class name
        public string MemberOf { get; set; }          // owning class/struct name
        public string Scope { get; set; }             // global, namespace, class
        public string Accessibility { get; set; }     // public, private, internal, protected, protected internal
        public bool IsStatic { get; set; }
        public bool IsAsync { get; set; }
        public bool IsAbstract { get; set; }
        public string GenericParams { get; set; }     // e.g. "<T, TResult>"
        public string SourcePreview { get; set; }
    }
}
