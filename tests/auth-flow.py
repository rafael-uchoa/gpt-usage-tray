import subprocess,json,threading,queue,os,tempfile,urllib.parse
exe=r'C:\Program Files\nodejs\node_modules\@openai\codex\node_modules\@openai\codex-win32-x64\vendor\x86_64-pc-windows-msvc\bin\codex.exe'
with tempfile.TemporaryDirectory(prefix='gpt-usage-auth-test-') as home:
 env=os.environ.copy();env['CODEX_HOME']=home
 for key in ['OPENAI_API_KEY','CODEX_API_KEY']:env.pop(key,None)
 p=subprocess.Popen([exe,'app-server','--stdio'],stdin=subprocess.PIPE,stdout=subprocess.PIPE,stderr=subprocess.DEVNULL,text=True,creationflags=0x08000000,env=env)
 q=queue.Queue()
 def reader():
  for line in p.stdout:
   try:q.put(json.loads(line))
   except:pass
 threading.Thread(target=reader,daemon=True).start()
 def call(i,method,params):
  p.stdin.write(json.dumps(dict(id=i,method=method,params=params))+'\n');p.stdin.flush()
  while True:
   msg=q.get(timeout=40)
   if msg.get('id')==i:
    if 'error' in msg:raise RuntimeError('Protocol request failed: '+method)
    return msg.get('result',{})
 try:
  call(1,'initialize',{'clientInfo':{'name':'gpt_usage_tray_auth_test','version':'1.0'}})
  p.stdin.write('{"method":"initialized","params":{}}\n');p.stdin.flush()
  assert call(2,'account/read',{'refreshToken':False})['account'] is None
  r=call(3,'account/login/start',{'type':'chatgpt'})
  u=urllib.parse.urlparse(r['authUrl'])
  assert u.scheme=='https' and u.hostname in ['auth.openai.com','chatgpt.com'] and r['loginId']
  call(4,'account/login/cancel',{'loginId':r['loginId']})
  assert call(5,'account/read',{'refreshToken':False})['account'] is None
  print('PASS real Codex signed-out detection, official browser login URL, login cancellation; existing sign-in untouched')
 finally:
  p.stdin.close()
  try:p.wait(timeout=5)
  except: p.kill();p.wait()
