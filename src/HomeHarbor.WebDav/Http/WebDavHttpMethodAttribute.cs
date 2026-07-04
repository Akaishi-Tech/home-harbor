using Microsoft.AspNetCore.Mvc.Routing;

namespace HomeHarbor.WebDav.Http;

public abstract class WebDavHttpMethodAttribute(string method, string template)
    : HttpMethodAttribute([method], template);

public sealed class HttpPropFindAttribute(string template)
    : WebDavHttpMethodAttribute(WebDavMethods.PropFind, template);

public sealed class HttpMkcolAttribute(string template)
    : WebDavHttpMethodAttribute(WebDavMethods.MkCol, template);

public sealed class HttpCopyAttribute(string template)
    : WebDavHttpMethodAttribute(WebDavMethods.Copy, template);

public sealed class HttpMoveAttribute(string template)
    : WebDavHttpMethodAttribute(WebDavMethods.Move, template);

public sealed class HttpLockAttribute(string template)
    : WebDavHttpMethodAttribute(WebDavMethods.Lock, template);

public sealed class HttpUnlockAttribute(string template)
    : WebDavHttpMethodAttribute(WebDavMethods.Unlock, template);

public sealed class HttpPropPatchAttribute(string template)
    : WebDavHttpMethodAttribute(WebDavMethods.PropPatch, template);

