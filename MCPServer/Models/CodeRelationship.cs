namespace MultiTerminal.MCPServer.Models
{
    public class CodeRelationship
    {
        public long Id { get; set; }
        public long FromId { get; set; }
        public long ToId { get; set; }
        public string Type { get; set; }           // calls, inherits, implements, overrides, references, uses_type, imports, subscribes, handles
        public string FilePath { get; set; }
        public int LineNumber { get; set; }
    }
}
