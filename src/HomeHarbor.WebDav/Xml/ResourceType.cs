using System.Xml.Serialization;

namespace HomeHarbor.WebDav.Xml;

public sealed class ResourceType
{
    [XmlElement("collection", Namespace = WebDavConstants.DavNamespace)]
    public string? CollectionMarker
    {
        get => IsCollection ? string.Empty : null;
        set => IsCollection = value is not null;
    }

    [XmlIgnore]
    public bool IsCollection { get; set; }
}

