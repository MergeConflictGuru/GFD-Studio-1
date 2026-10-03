"""Cascadeur command imported at startup: runs GFD AI scripts on scene idle."""
import json
import queue
import socket
import threading
import traceback
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from events import scene_idle_manager
import csc

_requests=queue.Queue()
_server=None
_subscription=None

def name():return 'GFD AI.Start bridge'
def description():return 'Starts the loopback script bridge used by GFD Studio AI blend'

class Handler(BaseHTTPRequestHandler):
    def log_message(self,*args):pass
    def reply(self,result):
        data=json.dumps(result).encode()
        self.send_response(200);self.send_header('Content-Type','application/json')
        self.send_header('Content-Length',str(len(data)));self.end_headers()
        try:self.wfile.write(data)
        except (BrokenPipeError,ConnectionError):pass
    def do_GET(self):self.reply({'ok':True,'version':'gfd-ai-1'})
    def do_POST(self):
        if self.path!='/run':self.send_error(404);return
        payload=json.loads(self.rfile.read(int(self.headers['Content-Length'])))
        request={'code':payload['code'],'done':threading.Event(),'result':None,'started':False}
        _requests.put(request)
        if request['done'].wait(30):self.reply(request['result'])
        else:self.reply({'ok':False,'error':'Timed out waiting for Cascadeur idle event'})

def run(scene):
    global _server
    if _server is not None:return
    try:_server=ThreadingHTTPServer(('127.0.0.1',8765),Handler)
    except OSError:
        # A manually started MCP bridge can already own this port.
        _server=False;return
    threading.Thread(target=_server.serve_forever,daemon=True).start()

def idle(scene):
    run(scene)
    # Each stage gets its own idle event, after Cascadeur updates the scene.
    if not _requests.empty():
        request=_requests.get();request['started']=True
        try:
            exec(request['code'],{'csc':csc,'scene':scene,'app':csc.app.get_application()})
            request['result']={'ok':True,'messages':[]}
        except Exception:request['result']={'ok':False,'error':traceback.format_exc()}
        request['done'].set()

def __reload_cleanup__():
    global _server
    if _server:
        _server.shutdown();_server.server_close()
    _server=None
    if _subscription is not None:
        try:scene_idle_manager.unsubscribe(_subscription)
        except KeyError:pass

_subscription=scene_idle_manager.subscribe(idle)
