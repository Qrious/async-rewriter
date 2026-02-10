#!/usr/bin/env python3
"""Local proxy helper for NuGet restore in Claude Code cloud sessions.

.NET's HttpClient has issues with very long proxy credentials (JWT tokens).
This script runs a local proxy that properly forwards Proxy-Authorization
headers to the upstream egress proxy.

Usage:
    python3 nuget-proxy-helper.py &
    export http_proxy=http://127.0.0.1:$(<~/.nuget-proxy-port)
    dotnet restore
"""

import base64
import http.server
import os
import select
import socket
import socketserver
import sys
import urllib.parse


def get_upstream_proxy():
    proxy_url = (
        os.environ.get("https_proxy")
        or os.environ.get("HTTPS_PROXY")
        or os.environ.get("http_proxy")
        or os.environ.get("HTTP_PROXY")
    )
    if not proxy_url:
        print("No upstream proxy configured, exiting.", file=sys.stderr)
        sys.exit(1)
    parsed = urllib.parse.urlparse(proxy_url)
    return parsed.hostname, parsed.port, parsed.username, parsed.password


UPSTREAM_HOST, UPSTREAM_PORT, UPSTREAM_USER, UPSTREAM_PASS = get_upstream_proxy()


class ProxyHandler(http.server.BaseHTTPRequestHandler):
    def log_message(self, format, *args):
        pass

    def do_CONNECT(self):
        upstream = socket.create_connection(
            (UPSTREAM_HOST, UPSTREAM_PORT), timeout=60
        )

        creds = f"{UPSTREAM_USER}:{UPSTREAM_PASS}"
        auth = base64.b64encode(creds.encode()).decode()

        connect_req = (
            f"CONNECT {self.path} HTTP/1.1\r\n"
            f"Host: {self.path}\r\n"
            f"Proxy-Authorization: Basic {auth}\r\n"
            f"\r\n"
        )
        upstream.sendall(connect_req.encode())

        response = b""
        while b"\r\n\r\n" not in response:
            chunk = upstream.recv(4096)
            if not chunk:
                break
            response += chunk

        status_line = response.split(b"\r\n")[0]
        if b"200" in status_line:
            self.send_response(200)
            self.end_headers()

            self.connection.setblocking(False)
            upstream.setblocking(False)

            while True:
                rlist = select.select([self.connection, upstream], [], [], 60)[0]
                if not rlist:
                    break
                for sock in rlist:
                    try:
                        data = sock.recv(65536)
                        if not data:
                            upstream.close()
                            return
                        if sock is self.connection:
                            upstream.sendall(data)
                        else:
                            self.connection.sendall(data)
                    except Exception:
                        upstream.close()
                        return
        else:
            self.send_error(502, f"Upstream proxy error: {status_line.decode()}")

        upstream.close()


if __name__ == "__main__":
    server = socketserver.ThreadingTCPServer(("127.0.0.1", 0), ProxyHandler)
    port = server.server_address[1]

    # Write port to file so the setup script can read it
    port_file = os.path.expanduser("~/.nuget-proxy-port")
    with open(port_file, "w") as f:
        f.write(str(port))

    print(f"NuGet proxy helper listening on 127.0.0.1:{port}", flush=True)
    server.serve_forever()
