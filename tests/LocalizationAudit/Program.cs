using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using LightWeaver.Localization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

var root = Path.GetFullPath(args.Length > 0 ? args[0] : ".");
var catalog = JsonSerializer.Deserialize<StringCatalog>(File.ReadAllText(Path.Combine(root, "localization", "catalog.json")), CatalogJson.Options)!;
var sources = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
var failures = new List<string>();
var calls = 0;
var xamlCalls = 0;
var allowedBrandText = new HashSet<string>(StringComparer.Ordinal) { "LightWeaver" };

foreach (var path in Directory.EnumerateFiles(Path.Combine(root, "src", "LightWeaver"), "*", SearchOption.AllDirectories)
    .Where(path => !path.Split(Path.DirectorySeparatorChar).Any(part => part is "bin" or "obj")))
{
    var relative = Path.GetRelativePath(root, path).Replace('\\', '/');
    if (path.EndsWith(".cs", StringComparison.Ordinal))
    {
        var syntax = CSharpSyntaxTree.ParseText(File.ReadAllText(path), new CSharpParseOptions(LanguageVersion.Preview));
        foreach (var assignment in syntax.GetRoot().DescendantNodes().OfType<AssignmentExpressionSyntax>())
        {
            var property = assignment.Left is MemberAccessExpressionSyntax access ? access.Name.Identifier.ValueText
                : assignment.Left is IdentifierNameSyntax name ? name.Identifier.ValueText : "";
            if (property is "Text" or "Content" or "Header" or "ToolTip" or "Title" or "StatusText")
                CheckDisplayText(assignment.Right, relative);
        }
        foreach (var call in syntax.GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            var methodName = call.Expression is MemberAccessExpressionSyntax methodAccess ? methodAccess.Name.Identifier.ValueText
                : call.Expression is IdentifierNameSyntax methodIdentifier ? methodIdentifier.Identifier.ValueText : "";
            if (methodName is "ShowToast" or "ShowError" or "SetName" or "AlertAsync" or "ConfirmAsync")
                foreach (var argument in call.ArgumentList.Arguments) CheckDisplayText(argument.Expression, relative);
            if (call.Expression is not MemberAccessExpressionSyntax { Expression: IdentifierNameSyntax { Identifier.ValueText: "L" } } member
                || member.Name.Identifier.ValueText is not ("Text" or "Format")) continue;
            // The markup extension forwards the key parsed from XAML, audited below.
            if (relative == "src/LightWeaver/Localization/TextExtension.cs") continue;
            calls++;
            var location = relative + ":" + (call.GetLocation().GetLineSpan().StartLinePosition.Line + 1);
            if (call.ArgumentList.Arguments.FirstOrDefault()?.Expression is not LiteralExpressionSyntax literal
                || !literal.IsKind(SyntaxKind.StringLiteralExpression))
            { failures.Add(location + " localization key must be a literal"); continue; }
            var key = literal.Token.ValueText;
            Record(key, relative, location);
            if (!catalog.Strings.TryGetValue(key, out var entry)) continue;
            try
            {
                var count = CompositeFormat.Parse(entry.Text).MinimumArgumentCount;
                var arguments = call.ArgumentList.Arguments.Count - 1;
                if (member.Name.Identifier.ValueText == "Format" && count != arguments)
                    failures.Add($"{location} {key}: format needs {count} args, call supplies {arguments}");
                if (member.Name.Identifier.ValueText == "Text" && count > 0)
                    failures.Add($"{location} {key}: Text used for a format template");
            }
            catch (FormatException) { failures.Add(location + " invalid composite format " + key); }

            foreach (var ancestor in call.Ancestors().OfType<InvocationExpressionSyntax>())
            {
                var method = ancestor.Expression.ToString();
                if (Regex.IsMatch(method, @"(^|\.)(FindResource|GetProperty|SetPropertyString|SetOption|StartsWith|EndsWith|Equals|Say|UiLog|Log|Detail|TryAddWithoutValidation|SetAutomationId)$")
                    || method.Contains("AppLog.", StringComparison.Ordinal))
                    failures.Add($"{location} translated text inside invariant call {method}");
            }
        }
    }
    else if (path.EndsWith(".xaml", StringComparison.Ordinal))
    {
        var doc = XDocument.Load(path, LoadOptions.SetLineInfo);
        foreach (var element in doc.Descendants())
        {
            foreach (var attribute in element.Attributes())
            {
                var value = attribute.Value;
                if (value.StartsWith("{loc:Text ", StringComparison.Ordinal))
                {
                    xamlCalls++;
                    Record(value[10..].TrimEnd('}').Trim(), relative, relative);
                    if (attribute.Name.LocalName is "AutomationId" or "Key" or "Name")
                        failures.Add(relative + " localization used as XAML identity " + attribute.Name);
                    continue;
                }
                var display = attribute.Name.LocalName is "Text" or "Content" or "Header" or "ToolTip" or "Title"
                    || attribute.Name.LocalName == "Value" && element.Attribute("Property")?.Value is "Text" or "Content" or "Header" or "ToolTip";
                if (display && value.Length > 0 && !value.StartsWith('{') && Regex.IsMatch(value, "[A-Za-z]") && !allowedBrandText.Contains(value))
                    failures.Add(relative + " hardcoded XAML UI text: " + value);
            }
            if (element.Name.LocalName is "Run" or "TextBlock" or "ComboBoxItem" or "Button" or "Label" or "CheckBox" or "RadioButton"
                && element.Nodes().OfType<XText>().Any(text => !string.IsNullOrWhiteSpace(text.Value)))
                failures.Add(relative + " hardcoded inline XAML UI text");
        }
    }
}

foreach (var (key, entry) in catalog.Strings)
{
    if (!sources.TryGetValue(key, out var used)) { failures.Add("unused catalog entry " + key); continue; }
    if (entry.Text.Length == 0 || string.IsNullOrWhiteSpace(entry.Context)) failures.Add("empty text or context " + key);
    if (!used.SetEquals(entry.Sources)) failures.Add("catalog source paths do not match callers " + key);
}

foreach (var failure in failures) Console.WriteLine("FAIL " + failure);
Console.WriteLine($"Localization audit: {calls} C# calls, {xamlCalls} XAML references, {catalog.Strings.Count} strings, {failures.Count} failures.");
return failures.Count == 0 ? 0 : 1;

void Record(string key, string source, string location)
{
    if (!catalog.Strings.ContainsKey(key)) failures.Add(location + " missing catalog key " + key);
    if (!sources.TryGetValue(key, out var used)) sources[key] = used = new(StringComparer.Ordinal);
    used.Add(source);
}

void CheckDisplayText(ExpressionSyntax expression, string source)
{
    foreach (var node in expression.DescendantNodesAndSelf())
    {
        var text = node is LiteralExpressionSyntax literal && literal.IsKind(SyntaxKind.StringLiteralExpression) ? literal.Token.ValueText
            : node is InterpolatedStringTextSyntax interpolated ? interpolated.TextToken.ValueText : "";
        if (!Regex.IsMatch(text, "[A-Za-z]") || allowedBrandText.Contains(text) || text == " (Debug)") continue;
        if (node.Ancestors().OfType<ConditionalExpressionSyntax>().Any(conditional => conditional.Condition.Span.Contains(node.Span))
            || node.Ancestors().OfType<SwitchExpressionArmSyntax>().Any(arm => arm.Pattern.Span.Contains(node.Span))) continue;
        // API/resource arguments describe identity and formatting, not rendered words.
        if (node.Ancestors().TakeWhile(ancestor => ancestor != expression.Parent).OfType<InvocationExpressionSyntax>().Any()) continue;
        if (node.Parent is InterpolationSyntax or InterpolationFormatClauseSyntax) continue;
        failures.Add(source + ":" + (node.GetLocation().GetLineSpan().StartLinePosition.Line + 1) + " hardcoded C# UI text: " + text);
    }
}
