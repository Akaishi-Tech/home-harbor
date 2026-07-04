namespace HomeHarbor.WebDav;

public static class WebDavConstants
{
    public const string DavNamespace = "DAV:";
    public const string DavNamespacePrefix = "d";
    public const string XmlContentType = "application/xml; charset=utf-8";

    public static class Headers
    {
        public const string Depth = "Depth";
        public const string Destination = "Destination";
        public const string Overwrite = "Overwrite";
        public const string Dav = "DAV";
    }

    public static class Depth
    {
        public const string Zero = "0";
        public const string One = "1";
        public const string Infinity = "infinity";
    }
}

