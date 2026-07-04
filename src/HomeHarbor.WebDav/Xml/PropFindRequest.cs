using System.Xml.Serialization;

namespace HomeHarbor.WebDav.Xml;

[XmlRoot("propfind", Namespace = WebDavConstants.DavNamespace)]
public sealed class PropFindRequest
{
    [XmlElement("allprop", Namespace = WebDavConstants.DavNamespace)]
    public string? AllProp { get; set; }

    [XmlElement("propname", Namespace = WebDavConstants.DavNamespace)]
    public string? PropName { get; set; }

    [XmlElement("prop", Namespace = WebDavConstants.DavNamespace)]
    public Prop? Prop { get; set; }
}

