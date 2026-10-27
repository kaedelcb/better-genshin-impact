// Reset regression through the responsive member-detail entry and actual legacy ack shape.
const assert = require('node:assert/strict');
const {fixture}=require('./web-gengdi-fixture.cjs');
(async()=>{const f=await fixture(),p=f.page;try{
 await f.login(); await p.evaluate(()=>noAck=true);
 await p.locator('[data-ui="detail"][data-uid="a"]').first().click();
 await p.locator('#detailReset').click(); await p.locator('.reset-state-cancel').click();
 assert.equal(await p.evaluate(()=>gatewayCalls.filter(x=>x.name==='control.batch.reset').length),0);
 await p.locator('#detailReset').click(); await p.locator('.reset-state-confirm').click();
 await p.waitForFunction(()=>document.querySelector('.reset-state-result').textContent.includes('等待执行端确认'));
 await p.evaluate(()=>receipt('b')); assert.equal(await p.locator('.reset-state-cancel').isDisabled(),true);
 await p.evaluate(()=>receipt('a')); await p.waitForFunction(()=>document.querySelector('.reset-state-result').textContent.includes('均已确认重置成功'));
 assert.equal(await p.locator('.reset-state-confirm').isVisible(),false); await p.locator('.reset-state-cancel').click();
 await p.locator('#detailReset').click(); await p.locator('.reset-state-all').check(); await p.locator('.reset-state-confirm').click();
 await p.waitForFunction(()=>document.querySelector('.reset-state-result').textContent.includes('等待执行端确认'));
 assert.deepEqual(await p.evaluate(()=>gatewayCalls.filter(x=>x.name==='control.sendCommand').at(-1).payload.command.target),['a','b']);
 await p.evaluate(()=>{receipt('a');receipt('b','failed')}); await p.waitForFunction(()=>document.querySelector('.reset-state-result').textContent.includes('成员乙：重置未完成'));
 assert.deepEqual(f.errors,[]); console.log('PASS reset UI: cancel, single/all, actual assistant ack routing, wrong UID rejected, partial failure, no real room.');
}finally{await f.close()}})().catch(e=>{console.error(e);process.exitCode=1});
