# ev-1 原始 TRX 完整性清单（SHA-256；摘录一致性核验锚）
摘录（ev1_trx_excerpts.md）由下列原始帧解析生成；解析逻辑＝读 TRX XML 的 Counters 与 UnitTestResult@outcome。
## 随本批提交（16 帧，打包 `_ev1/ev1_trx_archive.zip` 入库；解包后逐帧与下列 SHA-256 核对，提交后 `git show HEAD:_ev1/ev1_trx_archive.zip` 可取回）
- `_ev1_targeted_green.trx`  SHA-256=`1c2bb24b68204abbcb4b5fc91fecb45460f7e07d88ff94e3810d91fd6e08adcc`  26527 B
- `_ev1_notassembled_red.trx`  SHA-256=`7beac090687256f91aa2fa422c3762d0b3848c2e0e0d88fba37f32bbd0889b05`  23559 B
- `_ev1_nonvalid_red.trx`  SHA-256=`5f11b71de8332b37848a56912341528280eebf11d3112665a0a04b8e595a38f0`  25500 B
- `_ev1_mut1_red.trx`  SHA-256=`e975291cd22bd48dcb9e55c41a35af8b2aba2641a88cc852c40aadc9ebb0616e`  41104 B
- `_ev1_mut2_red.trx`  SHA-256=`8673e19627f067a31b42ccb67e53ee2167b08e5d9c2af763e06919b1d12d0a94`  36983 B
- `_ev1_mut3_red.trx`  SHA-256=`e2dbcf2a8ded4b109c185c155fb70fd86adf905c250286213c19e69f7b3b5378`  26029 B
- `_ev1_mut4_red.trx`  SHA-256=`2b6023b18c8512549d2a1603808feea622a748a9b9414654bb77c5afe1c133da`  35553 B
- `_ev1_mut5_red.trx`  SHA-256=`1454b4bc4332a741d5f42bf674c0ecbce771b9b99c6ec03532e6a23dd1058d37`  28866 B
- `_ev1_mut6_red.trx`  SHA-256=`b6a34a1e24a70940f01a57195ee2cab27e769408c3dbe04b5f80e3ad4c5a6e3a`  29330 B
- `_ev1_mut7_red.trx`  SHA-256=`0af8691d890f9e57b65bd6cb67987faa267ebe05af732e9bcdb38eaeda15c15f`  31074 B
- `_ev1_mut8_red.trx`  SHA-256=`729850d41b81253ba58534901c20e4101754568f51c946af7f47839f6392bb8b`  30936 B
- `_ev1_mut10_red.trx`  SHA-256=`cacab1e10f7c0100cabaab3f705f50d04bbf1bfc57402ce416b3ba1b3398248a`  48289 B
- `_ev1_mut11_red.trx`  SHA-256=`7b14da21d982729c82f0ebbb9180a8d54c598d3e3a432e2211b31223775a8622`  26093 B
- `_ev1_mut12_red.trx`  SHA-256=`c7c6b0b20fa95aad8461dd551e391a62b177ea2e1cf2ec36e822204db86e6082`  27437 B
- `_ev1_mut13_red.trx`  SHA-256=`af7048e3c2d765bfdb2afe8720519315582f56ff4948d3f112fe6cd5909aa7e2`  25500 B
- `_ev1_mut14_red.trx`  SHA-256=`90b84ad6e5a1f5f3d0bfb3d846c11664c22133c37d10e42c2acba9c7bfd0f2df`  31038 B

## 全量帧（2.3MB，体量原因不入库；留档未跟踪＋哈希锚定）

- `_ev1_full_final.trx`  SHA-256=`08015797b7deb25d9cf880b2b49a5ddb881716d385985fcbda099a63c9e7f031`  2303351 B——其计数与逐名差集已由 `_ev1/ev1_full_pernames.txt`（+15/−0 全名单）与摘录完整代表；本帧为留档副本，不入 git。
