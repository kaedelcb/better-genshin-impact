# ev3 TRX 帧清单与 SHA-256（生成于 2026-09-26；与 ev3_trx_archive.zip 逐条对应）

帧分组说明：green1＝初版夹具假阳性红帧（如实留档）；green2/green_r2＝夹具修正前后绿帧；
mutM*_red/red2＝第 1/2 轮夹具形态的突变红帧；green_final/green_final2＝第 1 轮处置后终帧；
green_final3＝ContainsKey 采纳帧（实测反例红，如实留档）；green_final3b＝撤回后终帧；
lane_probe1-10＝跨集合车道探针（F3 处置证据）；bgi_full_final/final2＝第 1 轮处置前后全量帧；
bgi_full_final3＝13/14 帧（基线项 WaitPointReport_SyncPointIdValidation 在全量上下文偶发通过，如实留档）；
bgi_full_final3b＝复核帧（14/14，与基线逐名差集双侧空）；collection_green＝TaskTakeoverIncident 集合级帧。

| 帧文件 | 字节数 | SHA-256 |
|---|---|---|
| _ev3_bgi_full_final.trx | 1828944 | `16d6e89f45ca7dd8cd2697971e4e4c06e98ed415faa69c1d0f9ecdf8c0917816` |
| _ev3_bgi_full_final2.trx | 1825621 | `0c8f5b368074856262d1596aef30eafe5489f6200c2ad0314594a207c059bdaf` |
| _ev3_bgi_full_final3.trx | 1824774 | `86f5586c0ce057d884e0d24f3ad8bdcf17ed02fa2270170ef609d639fd41bedb` |
| _ev3_bgi_full_final3b.trx | 1829401 | `c650893a0df69c5bf7c0970adcbda7d1c08da2ae6e937517770d94578e910ef8` |
| _ev3_collection_green.trx | 97559 | `76cbbc6db7fb47da923408f7372c67d5bcef3355601f022c41670d5afcbcb5ef` |
| _ev3_lane_probe1.trx | 105365 | `64cd0717055ff4c5ed30f1453dbf9aeef3445d178d3985ab0a0adc1ad26c7ded` |
| _ev3_lane_probe10.trx | 105365 | `621d4e57651d0011b4572b7728e1ad743750fd9440c4e2b90b9444ef023cb6d9` |
| _ev3_lane_probe2.trx | 105365 | `a6e42c32f159286abad5d8a5432bad916b8dce1bec16d510a85be3ac0ab91c3d` |
| _ev3_lane_probe3.trx | 105365 | `6043050b6b9f0c3a0050f96fbe1cd22f74376186412651d4b50a98bb05cc7724` |
| _ev3_lane_probe4.trx | 105365 | `f2f45b25f185597380d5be2fc50ed30b8d68b7afa3019288d760b65184e46954` |
| _ev3_lane_probe5.trx | 105365 | `af6e5df606cc56ab52524f11d001882bdd4f4055cab08f190fe7a8058597d51a` |
| _ev3_lane_probe6.trx | 105365 | `7e750a1c20c06db9a176f885f858809ef9b79f949e3f9ace5c471abe931d3af5` |
| _ev3_lane_probe7.trx | 105365 | `3f274f965f7b470693a98fba7993b7d7ce827f766b5acbb55d3034a5828cc3b7` |
| _ev3_lane_probe8.trx | 105365 | `26624872792779aada1b5ae0886bb2ee4f1cca049919435d268e3ed25ab4028e` |
| _ev3_lane_probe9.trx | 105365 | `2821bf2e96c3cde743d34799ff6e95bb3a10cf091425c16a81da420f84c2f462` |
| _ev3_mutM1_red.trx | 13227 | `b76eb8013cacef68212096af4eb5b308ebf26be8d058497cc95ef10d63874f8c` |
| _ev3_mutM1_red2.trx | 13227 | `44b8f170c30f129d9efadd2ee7c86ccbee6b141c510a82d6b1e909154df8fbef` |
| _ev3_mutM2_red.trx | 9155 | `3b0942f9bf1a45ffed6d3fdecf30880ca4ffd5fb7690454d13e7b94c813069d4` |
| _ev3_mutM2_red2.trx | 9155 | `2d2104b78671083cffa5b6c0c1cc507901e3aa9c507f039499a56f64f5467437` |
| _ev3_mutM3_red.trx | 8609 | `9396ace605703e3ab388d3bd3c13fe0ee3fcd1a7d0835b83eba22f18bea37793` |
| _ev3_mutM3_red2.trx | 8609 | `c4c887fd9be9f80e892562880229594fe258226a317be19e66550bef61eb99db` |
| _ev3_targeted_green1.trx | 9055 | `a641a5e4f6b08a213b26a5879b00ac9e79d2f3c5ec4e234aadab833054362e29` |
| _ev3_targeted_green2.trx | 6576 | `0cf50c459273de2f75e22baf5e6c05848e9b095460171da5118458874e3f4776` |
| _ev3_targeted_green_final.trx | 6576 | `9f8ddb43a7eae9d99225508de30f6eef6211b818cb0497be5dd59915f7d10141` |
| _ev3_targeted_green_final2.trx | 6576 | `26ebf0a96f9cf4dabf0f0b3a2175a579d622f13716988a69de7d28834642b134` |
| _ev3_targeted_green_final3.trx | 8519 | `99c25fc16cd3edf7704ba3cb55f1b5fe83f17b3c18e30d7d7cfd8391641be0a6` |
| _ev3_targeted_green_final3b.trx | 6576 | `ce2e0fee3bbdc12d02b5ff09027993a043536f7487031f6a7726596d8948b054` |
| _ev3_targeted_green_r2.trx | 6576 | `49f9ee3a577687dafafb74f17b49d5e67a43798fa29c684b2f0d10b4b2b369f2` |

共 28 帧。
