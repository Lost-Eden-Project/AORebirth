"""Cache one bounded public review source. Never reads credentials or runtime data."""
import argparse
import hashlib
import json
from pathlib import Path
import urllib.error
import urllib.request

parser = argparse.ArgumentParser()
parser.add_argument('--url', required=True)
parser.add_argument('--output', type=Path, required=True)
args = parser.parse_args()
receipt = args.output.with_suffix(args.output.suffix + '.receipt.json')
args.output.parent.mkdir(parents=True, exist_ok=True)
result = {'url': args.url, 'cacheFile': str(args.output), 'httpStatus': None}
if receipt.exists() and args.output.exists():
    prior = json.loads(receipt.read_text(encoding='utf-8'))
    if prior.get('sha256') == hashlib.sha256(args.output.read_bytes()).hexdigest() and prior.get('url') == args.url:
        print(json.dumps(prior))
        raise SystemExit(0)
try:
    request = urllib.request.Request(args.url, headers={'User-Agent': 'AORebirth bounded offline item family review', 'Accept':'text/html'})
    with urllib.request.urlopen(request, timeout=30) as response:
        content = response.read(4*1024*1024+1)
        if len(content)>4*1024*1024:
            raise ValueError('Source exceeded 4MiB bound')
        result.update(httpStatus=response.status, finalUrl=response.url, contentType=response.headers.get('Content-Type'))
    args.output.write_bytes(content)
    result.update(result='AVAILABLE', bytes=len(content), sha256=hashlib.sha256(content).hexdigest())
except urllib.error.HTTPError as error:
    result.update(result='UNAVAILABLE_HTTP', httpStatus=error.code, error=str(error))
except Exception as error:
    result.update(result='UNAVAILABLE', error=type(error).__name__+': '+str(error))
receipt.write_text(json.dumps(result,indent=2)+'\n',encoding='utf-8')
print(json.dumps(result))
