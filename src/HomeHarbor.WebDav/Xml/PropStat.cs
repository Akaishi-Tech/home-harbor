using System.Xml.Serialization;

namespace HomeHarbor.WebDav.Xml;

public sealed class PropStat
{
    [XmlElement("prop", Namespace = WebDavConstants.DavNamespace)]
    public Prop Prop { get; set; } = new();

    [XmlElement("status", Namespace = WebDavConstants.DavNamespace)]
    public string Status { get; set; } = string.Empty;
}

