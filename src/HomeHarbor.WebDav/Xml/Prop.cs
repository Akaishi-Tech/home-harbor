using System.Xml.Serialization;

namespace HomeHarbor.WebDav.Xml;

public sealed class Prop
{
    [XmlElement("creationdate", Namespace = WebDavConstants.DavNamespace)]
    public string? CreationDate { get; set; }

    [XmlElement("displayname", Namespace = WebDavConstants.DavNamespace)]
    public string? DisplayName { get; set; }

    [XmlElement("getcontentlength", Namespace = WebDavConstants.DavNamespace)]
    public string? GetContentLength { get; set; }

    [XmlElement("getcontenttype", Namespace = WebDavConstants.DavNamespace)]
    public string? GetContentType { get; set; }

    [XmlElement("getetag", Namespace = WebDavConstants.DavNamespace)]
    public string? GetETag { get; set; }

    [XmlElement("getlastmodified", Namespace = WebDavConstants.DavNamespace)]
    public string? GetLastModified { get; set; }

    [XmlElement("resourcetype", Namespace = WebDavConstants.DavNamespace)]
    public ResourceType? ResourceType { get; set; }

    [XmlElement("quota-available-bytes", Namespace = WebDavConstants.DavNamespace)]
    public string? QuotaAvailableBytes { get; set; }

    [XmlElement("quota-used-bytes", Namespace = WebDavConstants.DavNamespace)]
    public string? QuotaUsedBytes { get; set; }
}

