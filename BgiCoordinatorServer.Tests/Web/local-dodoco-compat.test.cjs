// 已缓存的旧网页脚本与新版 operations 混用时，远程告警仍使用原房间状态。
const vm=require('node:vm');const fs=require('node:fs');const path=require('node:path');const assert=require('node:assert/strict');
const source=fs.readFileSync(path.resolve(__dirname,'../../BgiCoordinatorServer/wwwroot/dodoco-operations.js'),'utf8');
const context=vm.createContext({roomReady:true,Set,clearTimeout,setTimeout});vm.runInContext(source,context);
assert.equal(vm.runInContext('watchIsLocal()',context),false);
assert.equal(vm.runInContext('watchTargetReady()',context),true);
context.roomReady=false;assert.equal(vm.runInContext('watchTargetReady()',context),false);
console.log('PASS mixed old/new remote page cache compatibility');
