const HOSTNAME_LABEL = /^[A-Za-z0-9](?:[A-Za-z0-9-]{0,61}[A-Za-z0-9])?$/;

/**
 * WireGuard accepts `host:port` or `[IPv6]:port`. Rejecting whitespace,
 * control characters, URL syntax, and extra config delimiters prevents a
 * user-supplied endpoint from becoming additional WireGuard directives.
 */
export function isSafeWireGuardEndpoint(value: string): boolean {
  if (!value || value.length > 512 || /[\s\u0000-\u001f\u007f]/.test(value)) {
    return false;
  }

  let host: string;
  let portValue: string;

  if (value.startsWith("[")) {
    const match = /^\[([0-9A-Fa-f:.]+)\]:(\d{1,5})$/.exec(value);
    if (!match || !match[1].includes(":")) return false;
    host = match[1];
    portValue = match[2];
  } else {
    const separator = value.lastIndexOf(":");
    if (separator <= 0 || value.indexOf(":") !== separator) return false;
    host = value.slice(0, separator);
    portValue = value.slice(separator + 1);
    if (
      host.length > 253 ||
      host.includes("..") ||
      !host.split(".").every((label) => HOSTNAME_LABEL.test(label))
    ) {
      return false;
    }
  }

  const port = Number(portValue);
  return Number.isInteger(port) && port >= 1 && port <= 65_535;
}
