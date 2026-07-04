using System.Xml;
using System.Xml.Serialization;

namespace HomeHarbor.WebDav.Xml;

public static class WebDavXml
{
    private static readonly XmlSerializerNamespaces Namespaces = CreateNamespaces();

    public static T? Deserialize<T>(Stream stream)
    {
        var serializer = new XmlSerializer(typeof(T));
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null
        };

        using var reader = XmlReader.Create(stream, settings);
        return serializer.Deserialize(reader) is T value ? value : default;
    }

    public static string Serialize<T>(T value)
    {
        var serializer = new XmlSerializer(typeof(T));
        var settings = new XmlWriterSettings
        {
            OmitXmlDeclaration = false,
            Encoding = new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            Indent = false
        };

        using var writer = new Utf8StringWriter();
        using var xml = XmlWriter.Create(writer, settings);
        serializer.Serialize(xml, value, Namespaces);
        return writer.ToString();
    }

    private static XmlSerializerNamespaces CreateNamespaces()
    {
        var namespaces = new XmlSerializerNamespaces();
        namespaces.Add(WebDavConstants.DavNamespacePrefix, WebDavConstants.DavNamespace);
        return namespaces;
    }

    private sealed class Utf8StringWriter : StringWriter
    {
        public override System.Text.Encoding Encoding => new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
    }
}
