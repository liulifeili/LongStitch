# GitHub 私人仓库同步

远端：`git@github.com:liulifeili/LongStitch.git`。

本地流程：完成改动 → 运行相关检查 → 只暂存相关文件 → 本地提交 → `.githooks/post-commit` 推送当前分支。

钩子不会上传未提交的编辑，不会强推，不会自动合并。推送失败仍保留本地提交，错误记录在 `.git/auto-push.log`，排除问题后执行 `git push` 重试。并发推送被跳过时同样需要检查是否有未上传提交。

当前电脑使用 Windows OpenSSH 和 SSH agent。项目级 `core.sshCommand` 只指定公钥身份，私钥由用户自行加载到 agent，AI 不读取私钥或口令。GitHub 主机公钥通过官方 HTTPS `/meta` 接口获取，保存在本机 `.git/github_known_hosts`，SSH 启用严格主机检查。

这些本机配置不随克隆传播。新电脑需要用户配置自己的 SSH agent、公钥身份和可信主机记录，再执行：

```powershell
git config --local core.hooksPath .githooks
```

`.gitignore` 继续排除构建产物、发布目录、导出结果和缓存。用户已允许当前未被忽略的截图及测试素材进入私人仓库。

加密私钥加 agent 能避免将私钥内容交给 AI，但不构成同一 Windows 账号内的文件访问隔离；可以访问 agent 的进程也可以在密钥权限内请求签名。
