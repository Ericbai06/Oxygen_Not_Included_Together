import json
from urllib import request
from urllib.parse import urlparse


PROTOCOL_VERSION = "2025-11-25"


def parse_mcp_response(text):
    if not text.strip():
        return {}
    if text.lstrip().startswith("{"):
        return json.loads(text)
    messages = [
        json.loads(line[5:].strip())
        for line in text.splitlines()
        if line.startswith("data:")
    ]
    if not messages:
        raise ValueError("MCP response contained no JSON message")
    return messages[-1]


class McpClient:
    def __init__(self, url, timeout=10):
        if urlparse(url).hostname not in {"127.0.0.1", "localhost", "::1"}:
            raise ValueError("MCP URL must use a loopback host")
        self.url = url
        self.timeout = timeout
        self.session_id = None
        self.next_id = 1

    def _post(self, payload):
        headers = {
            "Content-Type": "application/json",
            "Accept": "application/json, text/event-stream",
        }
        if self.session_id:
            headers["Mcp-Session-Id"] = self.session_id
            headers["Mcp-Protocol-Version"] = PROTOCOL_VERSION
        body = json.dumps(payload).encode()
        req = request.Request(self.url, body, headers=headers, method="POST")
        with request.urlopen(req, timeout=self.timeout) as response:
            self.session_id = response.headers.get("Mcp-Session-Id", self.session_id)
            return parse_mcp_response(response.read().decode("utf-8", errors="replace"))

    def initialize(self):
        result = self._call("initialize", {
            "protocolVersion": PROTOCOL_VERSION,
            "capabilities": {},
            "clientInfo": {"name": "oni-together-integration", "version": "1"},
        })
        self._post({"jsonrpc": "2.0", "method": "notifications/initialized"})
        return result

    def _call(self, method, params):
        call_id = self.next_id
        self.next_id += 1
        response = self._post({
            "jsonrpc": "2.0",
            "id": call_id,
            "method": method,
            "params": params,
        })
        if "error" in response:
            raise RuntimeError(json.dumps(response["error"], ensure_ascii=False))
        return response.get("result", {})

    def list_tools(self):
        return self._call("tools/list", {})

    def call_tool(self, name, arguments):
        result = self._call("tools/call", {"name": name, "arguments": arguments})
        if result.get("isError"):
            details = json.dumps(result, ensure_ascii=False)
            raise RuntimeError(f"MCP tool failed: {details}")
        return result
