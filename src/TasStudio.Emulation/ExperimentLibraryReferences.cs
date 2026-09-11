using System.Reflection;
using System.Xml.Linq;

namespace TasStudio.Emulation;

public static class ExperimentLibraryReferences
{
    public static string[] Validate(IEnumerable<string> libraries)
    {
        var paths = libraries.Select(p => Path.GetFullPath(Environment.ExpandEnvironmentVariables(p.Trim().Trim('"'))))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        foreach (var path in paths)
        {
            if (!File.Exists(path)) throw new FileNotFoundException($"Default experiment library not found: {path}. Update the libraries in Configuration → Interface.", path);
            var extension = Path.GetExtension(path);
            if (extension.Equals(".dll", StringComparison.OrdinalIgnoreCase))
            {
                // Reads metadata only; selecting a library never executes it.
                try { _ = AssemblyName.GetAssemblyName(path); }
                catch (BadImageFormatException) { throw new InvalidDataException($"Choose a managed .NET library: {path}"); }
            }
            else if (!extension.Equals(".csproj", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Experiment libraries must be C# projects (.csproj) or managed libraries (.dll).");
        }
        return paths;
    }

    internal static XElement[] Create(IEnumerable<string> libraries, string workspace)
    {
        var names = new HashSet<string>(["TasStudio.Sdk", "TasStudio.Core"], StringComparer.OrdinalIgnoreCase);
        return Validate(libraries).Select(path =>
        {
            var relative = Path.GetRelativePath(workspace, path);
            if (Path.GetExtension(path).Equals(".csproj", StringComparison.OrdinalIgnoreCase))
                return new XElement("ProjectReference", new XAttribute("Include", relative));
            var name = AssemblyName.GetAssemblyName(path).Name!;
            if (!names.Add(name)) throw new InvalidDataException($"Library {name} is already referenced. Select only one copy of each assembly; Studio supplies its SDK automatically.");
            return new XElement("Reference", new XAttribute("Include", name), new XElement("HintPath", relative), new XElement("Private", "true"));
        }).ToArray();
    }
}
