# 场景介绍
Computer Use Agent实现了通过简单的指令即可让远程计算机为用户执行任务，例如视频剪辑、演示文稿（PPT）制作以及自媒体账号运维等均能轻松完成。该方案基于自研的Doubao 1.5 UI - TARS模型，即“通过强化学习融合视觉能力与高级推理的模型”，能够直接与图形用户界面（GUI）进行交互，而无需依赖特定的应用程序编程接口（API）。

Computer Use Agent具备卓越的桌面应用操作能力，能够精准识别用户的任务需求，进行智能感知、自主推理并准确执行，体现了从“对话式人工智能（AI）”向“行动式人工智能（AI）”的转型趋势。
- 感知：CUA 截取计算机屏幕图像，旨在对数字环境中的内容进行情境化处理。这些视觉输入成为决策的依据。
- 推理：CUA 借助思维链推理对其观察结果进行评估，并跟踪中间步骤的进展。通过分析过往和当前的屏幕截图，该系统能够动态适应新的挑战和不可预见的变化。
- 行动：CUA 利用虚拟鼠标和键盘执行键入、点击和滚动等操作
<br>

## 开始体验
- 方式一：[基于火山FaaS应用模版一键部署ComputerUse应用](https://console.volcengine.com/vefaas/region:vefaas+cn-beijing/application/create?templateId=680b0a890e881f000862d9f0)

<video src="https://lf3-static.bytednsdoc.com/obj/eden-cn/lm_sth/ljhwZthlaukjlkulzlp/ark/assistant/videos/250519.mp4" controls>
</video>

- 方式二：[使用火山Computer-Use体验中心快速体验](https://console.volcengine.com/vefaas/region:vefaas+cn-beijing/market/computer)

<video src="https://lf3-static.bytednsdoc.com/obj/eden-cn/lm_sth/ljhwZthlaukjlkulzlp/ark/assistant/videos/20250415-221030.mp4" controls>
</video>

<br>

## 查看代码

```shell
git clone https://github.com/volcengine/ai-app-lab.git
cd demohouse/computer_use
```
<button data-btn-github="https://github.com/volcengine/ai-app-lab/tree/main/demohouse/computer_use">
 GitHub代码查看
</button>
<br>

# 架构图
![Image](https://lf3-static.bytednsdoc.com/obj/eden-cn/lm_sth/ljhwZthlaukjlkulzlp/ark/assistant/images/20250519-2057381.jpeg)

## 优势
- **强大的自研模型**：基于字节跳动自主研发的 Doubao 1.5 UI-TARS 核心模型，构建了行业领先的 GUI 语义理解引擎。通过自然语言处理与计算机视觉技术的深度融合，实现对用户指令的多维度语义解析，精准捕捉业务需求背后的场景意图，为复杂任务执行提供底层智能支撑。
- **多种操作系统支持**：提供 Windows 与 Linux 双系统支持，Windows具有无可比拟的传统软件生态，Linux更加轻量与灵活，可以满足企业级计算环境的多元化需求。。
- **极致拉起速度**：依托字节跳动分布式架构的底层技术积累，通过资源池化管理、热迁移变配等核心能力，实现云主机实例的秒级启动响应。动态负载均衡机制可根据业务流量实时调整资源分配，构建从资源申请到服务就绪的极致弹性链路，显著提升用户操作的实时性体验。
- **灵活服务组合**：采用高内聚低耦合的微服务架构设计，支持火山 computer-use 方案的全栈式部署与组件化调用，对与大型互联网客户，支持按需编排Agent Planer、MCP Server、Sandbox Manager等服务，对于小型客户，提供全栈式的一体化的解决方案。
<br>


# 关联模型及云产品
## 模型
| 相关服务              | 描述                                                              | 计费说明 |
|-----------------------|-----------------------------------------------------------------|--------|
| <a href="https://console.volcengine.com/ark/region:ark+cn-beijing/model/detail?Id=doubao-1-5-ui-tars" target="_blank">Doubao 1.5 UI-TARS</a> | UI-TARS 是一款原生面向图形界面交互（GUI）的Agent模型，通过感知、推理和行动等类人的能力，与GUI进行无缝交互。 | 开通赠送500,000 tokens免费额度，<br> 超过部分按 token 使用量付费，详见 <a href="https://www.volcengine.com/docs/82379/1544106">计费说明</a> |

## 云服务

| 相关服务                                                                      | 描述           | 计费说明                                                                                             |
|---------------------------------------------------------------------------|--------------|--------------------------------------------------------------------------------------------------|
| <a href="https://www.volcengine.com/product/ecs" target="_blank">云服务器</a> | 用于构建应用的沙箱环境。 | 支持多种计费方式，价格详见 <a href="https://www.volcengine.com/docs/6396/69812" target="_blank">计费说明</a>      |
| <a href="https://www.volcengine.com/product/vefaas" target="_blank">函数服务</a> | Serverless 全托管计算平台，支持快速创建部署函数。 | 支持多种计费方式，价格详见 <a href="https://www.volcengine.com/docs/6662/107454" target="_blank">计费说明</a>     |
| <a href="https://www.volcengine.com/product/tls" target="_blank">日志服务</a> | 提供针对日志类数据的一站式服务。 | 支持多种计费方式，价格详见 <a href="https://www.volcengine.com/docs/6470/1215813" target="_blank">计费说明</a>    |
| <a href="https://www.volcengine.com/product/apig" target="_blank">API网关</a> | 基于云原生、高扩展、高可用的云上网关托管服务。 | 采用按量计费的后付费方式，价格详见 <a href="https://www.volcengine.com/docs/6569/185249" target="_blank">计费说明</a> |


# 联系我们
点击加入飞书群： [应用实验室开发者沟通群](https://applink.larkoffice.com/client/chat/chatter/add_by_link?link_token=a5aq182d-ad9b-4867-8464-609f1ee8cb34)

<img src="https://p9-arcosite.byteimg.com/tos-cn-i-goo7wpa0wc/640fc3ff866649f5b7bff9c44874374b~tplv-goo7wpa0wc-image.image" alt="应用实验室开发者沟通飞书群" style="width:50%;">

<br><br><br><br><br><br>
