#:property ManagePackageVersionsCentrally=false
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

var path = args[0];
using var fs = File.OpenRead(path);
using var pe = new PEReader(fs);
var md = pe.GetMetadataReader();
foreach (var th in md.TypeDefinitions)
{
    var t = md.GetTypeDefinition(th);
    var name = md.GetString(t.Name);
    if (name != "SessionOptions" && name != "CoreMLFlags" && name != "NativeMethods") continue;
    Console.WriteLine($"TYPE {md.GetString(t.Namespace)}.{name}  attrs={t.Attributes}");
    foreach (var mh in t.GetMethods())
    {
        var m = md.GetMethodDefinition(mh);
        var mn = md.GetString(m.Name);
        if (mn.Contains("ExecutionProvider", StringComparison.Ordinal))
            Console.WriteLine($"  METHOD {mn}  attrs={m.Attributes}");
    }
    foreach (var fh in t.GetFields())
    {
        var f = md.GetFieldDefinition(fh);
        var fn = md.GetString(f.Name);
        if (name == "CoreMLFlags" || fn.Contains("ExecutionProvider", StringComparison.Ordinal))
            Console.WriteLine($"  FIELD {fn}");
    }
}
