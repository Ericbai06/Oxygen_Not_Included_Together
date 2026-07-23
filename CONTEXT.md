# ONI Together 同步测试

这个上下文描述 Mod 同步入口、执行证据和覆盖清单之间的关系。它用于区分“声明已覆盖”和“实际执行过”。

## Language

**同步入口（Sync Entry）**:
可能发送、接收、转发或应用联机状态的独立入口。稳定身份不依赖源码行号。
_Avoid_: 同步点、hook 数量

**执行声明（Execution Claim）**:
某次测试声明执行过一个同步入口。同一入口可被多个测试重复声明，因此声明数不能代表已覆盖入口数。
_Avoid_: mapped entry

**已映射入口（Mapped Entry）**:
至少绑定一个执行级测试的唯一同步入口。映射本身不等同于测试已经产生有效证据。
_Avoid_: claim、receipt 数

**执行证据（Execution Evidence）**:
由一次真实测试运行产生、并绑定当前目录和二进制身份的入口观察记录。
_Avoid_: 源码搜索结果、类名引用、静态计数

**可写覆盖清单（Coverage-ready Manifest）**:
所有同步入口均已映射，且对应执行证据满足所属运行层约束的覆盖清单。部分迁移结果不属于可写覆盖清单。
_Avoid_: partial export、coverage draft
