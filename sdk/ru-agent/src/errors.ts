/** Error carrying the JSON-RPC code and the original Ru code, when present. */
export class RuRpcError extends Error {
  constructor(
    message: string,
    readonly code: number,
    readonly ruCode?: string,
    readonly httpStatus?: number,
  ) {
    super(message);
    this.name = "RuRpcError";
  }
}

export class BridgeTransportError extends Error {
  constructor(message: string, readonly detail?: string) {
    super(message);
    this.name = "BridgeTransportError";
  }
}
