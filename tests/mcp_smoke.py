"""Exercise the real stdio server, including strict version rejection."""
import json
import pathlib
import select
import subprocess
import tempfile

ROOT = pathlib.Path(__file__).resolve().parents[1]
with tempfile.TemporaryDirectory(prefix="winix-mcp-") as state:
    process = subprocess.Popen(
        ["dotnet", str(ROOT / "src/Winix.Cli/bin/Debug/net10.0/winix.dll"), "mcp", "--home", state],
        stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.DEVNULL, text=True,
    )
    counter = 0
    def request(method, params):
        global counter
        counter += 1
        process.stdin.write(json.dumps({"jsonrpc": "2.0", "id": counter, "method": method, "params": params}) + "\n")
        process.stdin.flush()
        assert select.select([process.stdout], [], [], 10)[0], "MCP response timed out"
        response = json.loads(process.stdout.readline())
        assert response["id"] == counter
        return response

    def meta(version="2026-07-28"):
        return {"io.modelcontextprotocol/protocolVersion": version,
                "io.modelcontextprotocol/clientCapabilities": {},
                "io.modelcontextprotocol/clientInfo": {"name": "winix-test", "version": "1"}}

    try:
        old = request("initialize", {"protocolVersion": "2025-11-25", "capabilities": {}, "clientInfo": {"name": "old", "version": "1"}})
        assert old["error"]["code"] == -32022
        discovery = request("server/discover", {"_meta": meta()})["result"]
        assert discovery["supportedVersions"] == ["2026-07-28"]
        assert discovery["resultType"] == "complete"
        tools = request("tools/list", {"_meta": meta()})["result"]["tools"]
        names = [tool["name"] for tool in tools]
        assert len(names) == len(set(names))
        assert {"job_start", "job_wait", "job_dump", "desktop_action", "desktop_screenshot", "workspace_sync", "workspace_push", "workspace_pull",
                "vm_lease", "vm_device", "vm_devices", "vm_snapshot", "guest_driver_add", "guest_restart"} <= set(names)
        def properties(name):
            return set(next(tool for tool in tools if tool["name"] == name)["inputSchema"]["properties"])
        assert {"env", "args", "timeoutSeconds", "workspace", "label", "queueSeconds", "owner"} <= properties("job_start")
        assert {"wait", "waitTimeout", "idle"} <= properties("vm_lease")
        assert {"force", "owner"} <= properties("job_cancel")
        assert "workspace" in properties("workspace_sync")
        assert request("tools/list", {"_meta": meta()})["result"]["tools"] == tools
        wrong = request("tools/list", {"_meta": meta("2025-11-25")})
        assert "error" in wrong
        missing = request("tools/list", {})
        assert "error" in missing
        response = request("tools/call", {"_meta": meta(), "name": "vm_list", "arguments": {}})
        assert json.loads(response["result"]["content"][0]["text"]) == []
        unknown = request("tools/call", {"_meta": meta(), "name": "does_not_exist", "arguments": {}})
        assert "error" in unknown or unknown["result"].get("isError")
        lease = request("tools/call", {"_meta": meta(), "name": "vm_lease", "arguments": {"vm": "dev", "action": "acquire", "owner": "smoke", "ttl": "5m"}})
        assert json.loads(lease["result"]["content"][0]["text"])["owner"] == "smoke"
        status = request("tools/call", {"_meta": meta(), "name": "vm_lease", "arguments": {"vm": "dev", "action": "status"}})
        status = json.loads(status["result"]["content"][0]["text"])
        assert status["lease"]["owner"] == "smoke" and status["waiting"] == [] and status["lease"]["idleSeconds"] == 1800
        denied = request("tools/call", {"_meta": meta(), "name": "vm_lease", "arguments": {"vm": "dev", "action": "release", "owner": "other"}})
        assert denied["result"].get("isError") and "vm_leased" in denied["result"]["content"][0]["text"]
        released = request("tools/call", {"_meta": meta(), "name": "vm_lease", "arguments": {"vm": "dev", "action": "release", "owner": "smoke"}})
        assert json.loads(released["result"]["content"][0]["text"])["released"] is True
        print(f"MCP smoke passed: strict revision, discovery, {len(names)} tools, stable catalog, tool output, errors, leases")
    finally:
        process.terminate()
        process.wait(timeout=10)
