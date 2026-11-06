using System.Collections.Concurrent;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Jellyfin.Plugin.ApiKeyMiddleware.Builder;

// <summary> texts of controller actions, read from the XML documentation file next to the assembly
// (Jellyfin ships Jellyfin.Api.xml; Swashbuckle reads the same file for the OpenAPI document).
// Assemblies without a documentation file simply have no summaries.
public sealed partial class XmlDocSummaries
{
    private readonly ConcurrentDictionary<Assembly, IReadOnlyDictionary<string, string>> _byAssembly = new();

    public string? Get(MethodInfo method)
    {
        var docs = _byAssembly.GetOrAdd(method.Module.Assembly, Load);
        return Lookup(docs, method);
    }

    internal static string? Lookup(IReadOnlyDictionary<string, string> docs, MethodInfo method)
    {
        if (docs.TryGetValue(MemberId(method), out var summary))
        {
            return summary;
        }

        // Fallback when the generated id does not match exactly: use the only member with this name.
        var prefix = "M:" + TypeName(method.DeclaringType!) + "." + method.Name;
        var candidates = docs.Where(d => d.Key == prefix || d.Key.StartsWith(prefix + "(", StringComparison.Ordinal)).Take(2).ToList();
        return candidates.Count == 1 ? candidates[0].Value : null;
    }

    internal static IReadOnlyDictionary<string, string> Parse(XDocument document)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var member in document.Descendants("member"))
        {
            var name = member.Attribute("name")?.Value;
            var summary = member.Element("summary");
            if (name is null || summary is null || !name.StartsWith("M:", StringComparison.Ordinal))
            {
                continue;
            }

            var text = Render(summary);
            if (text.Length > 0)
            {
                result[name] = text;
            }
        }

        return result;
    }

    // Documentation id of a method, e.g.
    // M:Ns.Type.Method(System.Nullable{System.Guid},System.Guid[],System.Collections.Generic.IReadOnlyList{System.String})
    internal static string MemberId(MethodInfo method)
    {
        var id = new StringBuilder("M:").Append(TypeName(method.DeclaringType!)).Append('.').Append(method.Name);
        if (method.IsGenericMethodDefinition)
        {
            id.Append("``").Append(method.GetGenericArguments().Length);
        }

        var parameters = method.GetParameters();
        if (parameters.Length > 0)
        {
            id.Append('(').AppendJoin(',', parameters.Select(p => ParameterTypeName(p.ParameterType))).Append(')');
        }

        return id.ToString();
    }

    private static string TypeName(Type type)
        => (type.FullName ?? type.Name).Replace('+', '.');

    private static string ParameterTypeName(Type type)
    {
        if (type.IsByRef)
        {
            return ParameterTypeName(type.GetElementType()!) + "@";
        }

        if (type.IsArray)
        {
            return ParameterTypeName(type.GetElementType()!) + "[" + new string(',', type.GetArrayRank() - 1) + "]";
        }

        if (type.IsGenericParameter)
        {
            return (type.DeclaringMethod is null ? "`" : "``") + type.GenericParameterPosition;
        }

        if (type.IsGenericType)
        {
            var definition = type.GetGenericTypeDefinition();
            var name = TypeName(definition);
            name = name[..name.IndexOf('`', StringComparison.Ordinal)];
            return name + "{" + string.Join(',', type.GetGenericArguments().Select(ParameterTypeName)) + "}";
        }

        return TypeName(type);
    }

    private static string Render(XElement element)
    {
        var text = new StringBuilder();
        foreach (var node in element.Nodes())
        {
            switch (node)
            {
                case XText t:
                    text.Append(t.Value);
                    break;
                case XElement e when e.Attribute("cref") is { } cref:
                    // "T:Jellyfin.Data.Entities.User" -> "User", "M:Ns.Type.Method(System.Guid)" -> "Method"
                    var target = cref.Value.Split('(')[0];
                    text.Append(target[(target.LastIndexOf('.') + 1)..]);
                    break;
                case XElement e when e.Attribute("langword") is { } langword:
                    text.Append(langword.Value);
                    break;
                case XElement e when e.Attribute("name") is { } name && e.IsEmpty:
                    text.Append(name.Value);
                    break;
                case XElement e:
                    text.Append(Render(e));
                    break;
            }
        }

        return WhitespaceRegex().Replace(text.ToString(), " ").Trim();
    }

    private static IReadOnlyDictionary<string, string> Load(Assembly assembly)
    {
        try
        {
            var path = Path.ChangeExtension(assembly.Location, ".xml");
            return string.IsNullOrEmpty(assembly.Location) || !File.Exists(path)
                ? new Dictionary<string, string>()
                : Parse(XDocument.Load(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException)
        {
            return new Dictionary<string, string>();
        }
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();
}
