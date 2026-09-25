# ev2 TRX SHA-256 完整性清单（18 帧，权威帧数以本清单为准）

本批 18 帧全部打包随批入库（_ev2/ev2_trx_archive.zip）；松散帧留在 TestResults/ 未跟踪。

| 文件 | 字节数 | SHA-256 |
|---|---|---|
| _ev2_targeted_green.trx | 19156 | 1b53285afb7a3614f01943117677ff1f9ee6995f48320016ecc27ade7c01e225 |
| _ev2_targeted_green2.trx | 19156 | 74ddb716c112517a35ca4fbc123d6be1a74669e8a7a39d9dda445c5ffc46b15c |
| _ev2_targeted_green3.trx | 19156 | c3d2db881bd3f8a3893697a61ee85f9cd38946d8d7add0d53c25e913ec8e9b0a |
| _ev2_targeted_green4.trx | 19156 | 857f9cb97cdcbf36212610407210d3fa2ce622ec5f40863a7c2e8272d38d88e3 |
| _ev2_targeted_green5.trx | 19156 | aeab9a440123f04b4f7d064f5737ec81d006a06bb9ae49e914f1cc11d9086b93 |
| _ev2_targeted_green6.trx | 19156 | 7d0d5ef43ff867108b71268b4b1660b287a1f70f011a93908e3b95d59834c5bf |
| _ev2_mutM1_red.trx | 33444 | 00d28e1ef38e7f1e020a6d0c0d335bd5a8fc0f45be308da77f398f1878e37be3 |
| _ev2_mutM2_red.trx | 29561 | 6e6fb12285148a9617e6f3082161a26c5b0eabe683ea0e86637af4ca5d0ec317 |
| _ev2_mutM3_red.trx | 21322 | ad1894273b9fd34ebd6d8078526a5c31aa8fc969bf5e7af5d31ffd5e25a4158c |
| _ev2_mutM4_red.trx | 21259 | 6396f8c1cc10b7de2d772e317a50d38515606805d83f4c998e0d61ab4589dbf5 |
| _ev2_mutM5_red.trx | 21456 | e65fbba17734839400cf842d2b36533c12ca426f0d873583545024a0e7a4bd23 |
| _ev2_mutM6_red.trx | 26069 | 3353b7bec0d7dd129c2aace475f5e97477b3f55197ed0db61eeaa39b375fc670 |
| _ev2_mutM7_red.trx | 21200 | b9e8ae106fa44736e18267a88f45bf8c7ff5a0917526471a8de2a622be0fe6ee |
| _ev2_mutM6_jobregistry_red.trx | 15973 | 615b8f2e2388f48337fcaa9eb9e2225d100e0195af7cf257ab9ed2530e7bf3ee |
| _ev2_bgi_full_final2.trx | 1821256 | ce67e74d564f7f378129fa39734e8262ad9dac80b08c86596ad3b5a547857450 |
| _ev2_bgi_full_final3.trx | 1823013 | 35738a3dbfad8bb728b8a341bebfbdc6aaf0f1cffd9f52a99a7d94df63f50733 |
| _ev2_bgi_full_final4.trx | 1824299 | 21a90d2ac8fbb86b35fe9aadc4ac245888837b89d14ab041b11972a3dfd8efa7 |
| _ev2_bgi_full_final5.trx | 1820504 | 7b39c7ceed30d3efb753c65674e1ae0fb1e80b5c6dc1a7b8c306c9b0d567a22c |

- ev2_trx_archive.zip 自锚：字节数 1224666、SHA-256 `8f239385dec95107b40b15cbc0caab55c3fe0dbb8d71db9529f7a87bc5004fa5`

## 逐行解压比对（zip 内条目 vs 上表，生成时机械执行）
- _ev2_targeted_green.trx：一致
- _ev2_targeted_green2.trx：一致
- _ev2_targeted_green3.trx：一致
- _ev2_targeted_green4.trx：一致
- _ev2_targeted_green5.trx：一致
- _ev2_targeted_green6.trx：一致
- _ev2_mutM1_red.trx：一致
- _ev2_mutM2_red.trx：一致
- _ev2_mutM3_red.trx：一致
- _ev2_mutM4_red.trx：一致
- _ev2_mutM5_red.trx：一致
- _ev2_mutM6_red.trx：一致
- _ev2_mutM7_red.trx：一致
- _ev2_mutM6_jobregistry_red.trx：一致
- _ev2_bgi_full_final2.trx：一致
- _ev2_bgi_full_final3.trx：一致
- _ev2_bgi_full_final4.trx：一致
- _ev2_bgi_full_final5.trx：一致
- 比对结果：18/18 一致（与台账 evidence_self_verification 同口径）。
