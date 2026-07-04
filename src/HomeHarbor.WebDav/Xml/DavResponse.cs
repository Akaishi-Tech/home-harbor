using System.Xml.Serialization;

namespace HomeHarbor.WebDav.Xml;

public sealed class DavResponse
{
    [XmlElement("href", Namespace = WebDavConstants.DavNamespace)]
    public string Href { get; set; } = string.Empty;

    [XmlElement("propstat", Namespace = WebDavConstants.DavNamespace)]
    public List<PropStat> PropStats { get; set; } = [];
}

