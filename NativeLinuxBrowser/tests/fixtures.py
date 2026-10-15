from http.server import SimpleHTTPRequestHandler, ThreadingHTTPServer
import functools
from pathlib import Path
import tempfile
import threading


class RangeHandler(SimpleHTTPRequestHandler):
    def log_message(self, *_):
        pass

    def send_head(self):
        self.remaining = None
        if not self.path.startswith("/video.mp4"):
            return super().send_head()
        file = open(Path(self.directory) / "video.mp4", "rb")
        size = file.seek(0, 2)
        begin, end = 0, size - 1
        range_header = self.headers.get("Range")
        if range_header:
            try:
                unit, value = range_header.split("=", 1)
                first, last = value.split("-", 1)
                assert unit == "bytes" and first and "," not in value
                begin = int(first)
                end = min(int(last), end) if last else end
                assert 0 <= begin <= end < size
            except (AssertionError, ValueError):
                file.close()
                self.send_error(416)
                return None
        self.send_response(206 if range_header else 200)
        self.send_header("Content-Type", "video/mp4")
        self.send_header("Accept-Ranges", "bytes")
        self.send_header("Content-Length", str(end - begin + 1))
        if range_header:
            self.send_header("Content-Range", f"bytes {begin}-{end}/{size}")
        self.end_headers()
        file.seek(begin)
        self.remaining = end - begin + 1
        return file

    def copyfile(self, source, outputfile):
        try:
            if self.remaining is None:
                return super().copyfile(source, outputfile)
            while self.remaining:
                data = source.read(min(self.remaining, 65536))
                if not data:
                    break
                outputfile.write(data)
                self.remaining -= len(data)
        except (BrokenPipeError, ConnectionResetError):
            pass  # Media clients cancel old range requests on seeks.


class VideoFixture:
    def __init__(self, fixture, mse=False, codecs="avc1.42c020,mp4a.40.2"):
        self.directory = Path(tempfile.mkdtemp(prefix="crystalcast-video-"))
        (self.directory / "video.mp4").symlink_to(Path(fixture).resolve())
        src = """const source=new MediaSource();v.src=URL.createObjectURL(source);source.addEventListener('sourceopen',async()=>{
try { const b=source.addSourceBuffer('video/mp4; codecs="CODECS"');const data=await(await fetch('/video.mp4')).arrayBuffer();
b.addEventListener('updateend',()=>{if(source.readyState==='open'){source.endOfStream();v.play()}} ,{once:true});b.appendBuffer(data);
}catch(e){document.title='MSE ERROR '+e.message}});""".replace("CODECS", codecs) if mse else "v.src='/video.mp4';v.play();"
        (self.directory / "video.html").write_text("""<!doctype html><html><head><title>CrystalCast local H264 AAC fixture</title></head>
<body style='margin:0;background:black'><video id='v' muted autoplay loop playsinline style='width:100vw;height:100vh'></video>
<script>const v=document.getElementById('v');""" + src + "</script></body></html>")
        self.server = ThreadingHTTPServer(("127.0.0.1", 0), functools.partial(RangeHandler, directory=str(self.directory)))
        threading.Thread(target=self.server.serve_forever, daemon=True).start()
        self.url = f"http://127.0.0.1:{self.server.server_port}/video.html"

    def close(self):
        self.server.shutdown()
