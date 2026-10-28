import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import vm from 'node:vm';

// 测试请求关联与断线边界：网关转发 ACK 不能冒充成员完成。
const source = await readFile(new URL('../../BgiCoordinatorServer/wwwroot/dodoco-web.js', import.meta.url), 'utf8');
const status={textContent:''};
const context = vm.createContext({
  document: { getElementById: id => id==='dodocoStatus'?status:null }, setTimeout, clearTimeout, clearInterval, console,
  players: [{playerUid:'alice',online:true}], roomReady:true, roomCode:'ABC123',
  connection: {invoke: async () => ({payload:{ack:true}})},
  makeEnvelope: (_type,name,payload) => ({name,payload}), checkGatewayError: () => {},
  newRequestId: (() => { let id=0; return () => `test${++id}`; })(), escapeHtml: String,
});
vm.runInContext(source + '\nthis.api={dodoco,dodocoWait,dodocoEvent,dodocoCancelPending,dodocoRequest,dodocoConnection,dodocoStatus};', context);
const {dodoco,dodocoWait,dodocoEvent,dodocoCancelPending,dodocoRequest,dodocoConnection,dodocoStatus} = context.api;
dodoco.target='alice';
const pending=dodocoRequest('watch.read');
await new Promise(resolve => setImmediate(resolve));
let completed=false; pending.then(()=>{completed=true;});
await new Promise(resolve => setImmediate(resolve));
assert.equal(completed,false,'gateway ACK must not resolve a member operation');
dodocoEvent({name:'dodoco.result',payload:{requestId:'test1',status:'ok',data:{value:1}}});
assert.equal((await pending).value,1);

const frame=dodocoWait('frame','alice','frame1',1000);
dodocoEvent({name:'screenshot.member',payload:{uid:'alice',jpegBase64:'abc'}});
assert.equal(dodoco.pending.has('frame1'),true,'uncorrelated old screenshot is not accepted');
dodocoEvent({name:'screenshot.member',payload:{uid:'bob',requestId:'frame1',jpegBase64:'abc'}});
assert.equal(dodoco.pending.has('frame1'),true,'another member frame is not accepted');
dodocoEvent({name:'screenshot.member',payload:{uid:'alice',requestId:'frame1',jpegBase64:'abc'}});
assert.equal((await frame).uid,'alice');

const download=dodocoWait('download','alice','file1',1000);
dodoco.pending.get('file1').fileName='file.log';
dodocoEvent({name:'log.fileChunk',payload:{uid:'alice',requestId:'file1',fileName:'file.log',seq:1,totalChunks:2,chunkBase64:'YQ==',done:true}});
await assert.rejects(download,/缺失|无效/);
const disconnect=dodocoWait('files','alice','files1',1000);
dodocoCancelPending('连接已断开');
await assert.rejects(disconnect,/断开/);
const reconnect=dodocoWait('files','alice','files2',1000);
dodocoConnection(false);
await assert.rejects(reconnect,/断开/);
dodocoConnection(true);dodocoStatus('已连接房间。');
assert.match(status.textContent,/先前请求因断线结果未确认/);
console.log('dodoco protocol correlation and disconnect checks passed');
