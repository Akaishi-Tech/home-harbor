using System.Xml.Serialization;

namespace HomeHarbor.WebDav.Xml;

[XmlRoot("multistatus", Namespace = WebDavConstants.DavNamespace)]
public sealed class MultiStatus
{
    [XmlElement("response", Namespace = WebDavConstants.DavNamespace)]
    public List<DavResponse> Responses { get; set; } = [];
}

