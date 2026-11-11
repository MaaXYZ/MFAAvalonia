# External Notifications (User Fill Guide)

This page explains **how to fill the fields**. Use **Send Test** to verify after filling.

---

## DingTalk

**How to fill**
- **Bot Token**: paste the full webhook URL (e.g. `https://oapi.dingtalk.com/robot/send?access_token=xxx`) or just the `access_token`
- **Secret**: signing secret (leave empty if signing is disabled)

**Behavior**
- With `Secret` → signed request  
- Without `Secret` → normal webhook

---

## Email

**How to fill**
- **Email Address**: your email address (e.g. `xxx@qq.com`)
- **SMTP App Password / Authorization Code**: your email provider’s app password or SMTP authorization code (not login password)

**Behavior**
- SMTP server is auto-selected by email domain

---

## Lark

Lark has two ways to fill:

### Method A: Webhook (simple)
- **Webhook URL**: paste the bot webhook

**Behavior**: If Webhook URL is provided, it is used directly.

### Method B: Signed (normal)
- **Lark ID**: bot ID  
- **Bot Token**: bot secret

**Behavior**: Used only when Webhook URL is empty.

---

## WxPusher

Two ways to fill:

### Method A: Normal
- **Bot Token**: AppToken  
- **UID**: user UID

### Method B: Simple
- **Bot Token**: leave empty  
- **UID**: put SPT here

---

## Telegram

**How to fill**
- **Chat ID**: target chat or group ID  
- **Bot Token**: bot token

---

## Discord Bot

**How to fill**
- **Channel ID**: channel ID  
- **Bot Token**: bot token

---

## Discord Webhook

**How to fill**
- **Webhook URL**: webhook link  
- **Webhook Name**: display name (optional)

---

## OneBot

**How to fill**
- **Server**: OneBot server URL (e.g. `http://127.0.0.1:5700`)  
- **Key**: auth key (if required)  
- **User**: target QQ number

**Note**
- This provider is not wired into the unified send flow yet.

---

## SMTP

**How to fill**
- **SMTP Server**: server address (e.g. `smtp.qq.com`)  
- **SMTP Port**: port (common 465/587/25)  
- **SMTP SSL**: SSL toggle  
- **SMTP Auth**: whether auth is required  
- **SMTP User**: username (only if Auth is on)  
- **SMTP Password**: password/app password (only if Auth is on)  
- **SMTP From**: sender address  
- **SMTP To**: recipient address

---

## Qmsg

**How to fill**
- **Server**: Qmsg server URL  
- **Key**: Qmsg key  
- **User**: receiver QQ  
- **Bot**: bot QQ (optional)

---

## ServerChan

**How to fill**
- **Send Key**: ServerChan SendKey

**Behavior**
- Supports `sctp` format SendKey

---

## Custom Webhook

**How to fill**
- **Webhook URL**: full `http/https` URL  
- **Content Type**: default `application/json`  
- **Payload Template**: use `{message}` as placeholder

**Template example**
```
{"text":"{message}"}
```

---

## Troubleshooting

- **No message**: check required fields and network access  
- **Webhook error**: must be full `http/https` URL  
- **Email failure**: most providers require app password
