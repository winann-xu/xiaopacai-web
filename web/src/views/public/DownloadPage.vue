<template>
  <div class="download-page">
    <header class="dl-header">
      <div class="dl-brand">
        <span class="dl-logo">🐤</span>
        <span class="dl-title">小趴菜 · 下载中心</span>
      </div>
      <div class="dl-actions">
        <el-button v-if="loggedIn" text @click="go('/dashboard')">返回控制台</el-button>
        <el-button v-else text @click="go('/login')">登录 / 管理后台</el-button>
      </div>
    </header>

    <main class="dl-main">
      <h1 class="dl-heading">下载最新客户端</h1>
      <p class="dl-sub">小趴菜守护 · 家长监控客户端（Android / Windows / iOS 陆续上线）</p>

      <div class="dl-grid">
        <el-card class="dl-card" shadow="hover" v-loading="loading">
          <div class="dl-icon">📱</div>
          <h3>Android 客户端</h3>
          <p class="dl-desc">儿童守护 / 家长端二合一<br />Android 8.0 及以上</p>
          <template v-if="stableLatest">
            <p class="dl-meta">
              最新版本 <b>v{{ stableLatest.versionName }}</b>
              <span v-if="stableLatest.minVersionCode"> · 低于 v{{ codeToVersion(stableLatest.minVersionCode) }} 将强制更新</span>
            </p>
            <p v-if="stableLatest.changelog" class="dl-changelog">{{ stableLatest.changelog }}</p>
            <el-button
              v-for="abi in ABIS"
              :key="abi"
              :type="abi === 'arm64-v8a' ? 'primary' : ''"
              size="large"
              class="dl-btn"
              :class="{ 'dl-btn-sub': abi !== 'arm64-v8a' }"
              :disabled="!stableUrls[abi]"
              @click="download(stableUrls[abi])"
            >
              下载 APK（{{ abi }}{{ abi === 'arm64-v8a' ? ' · 推荐' : abi === 'x86_64' ? ' · 模拟器' : '' }}）
            </el-button>
            <p class="dl-meta">按手机芯片选择；安装前客户端会自动校验 SHA-256</p>
          </template>
          <template v-else-if="!loading">
            <p class="dl-meta">暂未发布新版本，请稍后再来</p>
          </template>
        </el-card>

        <el-card v-if="specialLatest" class="dl-card dl-card-special" shadow="hover">
          <div class="dl-icon">🛡️</div>
          <h3>特别版（限制机型专用）</h3>
          <p class="dl-desc">
            ColorOS 等限制第三方 Device Owner 的机型<br />testkey 签名 · 强管制模式可用
          </p>
          <p class="dl-meta">
            最新版本 <b>v{{ specialLatest.versionName }}</b>
          </p>
          <p v-if="specialLatest.changelog" class="dl-changelog">{{ specialLatest.changelog }}</p>
          <el-button
            v-for="abi in ABIS"
            :key="abi"
            type="warning"
            size="large"
            class="dl-btn"
            :disabled="!specialUrls[abi]"
            @click="download(specialUrls[abi])"
          >
            下载特别版（{{ abi }}）
          </el-button>
          <p class="dl-meta dl-warn">
            特别版与正式版签名不同，两者不能互相覆盖安装；切换渠道需先卸载再装。
            特别版后续更新只走特别版渠道，自动升级不会串到正式版。
          </p>
        </el-card>

        <el-card class="dl-card dl-card-disabled" shadow="hover">
          <div class="dl-icon">💻</div>
          <h3>Windows 桌面端</h3>
          <p class="dl-desc">家长端桌面客户端<br />即将上线，敬请期待</p>
          <p class="dl-meta">期待上线</p>
          <el-button size="large" class="dl-btn" disabled>期待上线</el-button>
        </el-card>

        <el-card class="dl-card dl-card-disabled" shadow="hover">
          <div class="dl-icon">🍎</div>
          <h3>iOS 客户端</h3>
          <p class="dl-desc">iPhone / iPad<br />即将上线，敬请期待</p>
          <p class="dl-meta">期待上线</p>
          <el-button size="large" class="dl-btn" disabled>期待上线</el-button>
        </el-card>

        <el-card class="dl-card" shadow="hover">
          <div class="dl-icon">🛠️</div>
          <h3>电脑一键授权脚本</h3>
          <p class="dl-desc">孩子手机快速开通守护权限<br />Windows · 双击运行 · 约30秒</p>
          <p class="dl-meta">BAT · 使用方法见小趴菜家长端首页</p>
          <el-button type="success" size="large" class="dl-btn" @click="download('/downloads/xiaopacai-adb-grant.bat')">
            下载脚本
          </el-button>
        </el-card>
      </div>

      <footer class="dl-footer">
        <span>小趴菜 · 开源免费 · 本地优先 · 数据不上云</span>
      </footer>
    </main>
  </div>
</template>

<script setup lang="ts">
// 小趴菜 Web 3.0 — 下载中心（登录前后均可访问）
// [TASK-APP-UPDATE-V1] B2：由静态页升级为「更新清单驱动」——versionCode=0 查询最新已发布版本，
// 展示版本号/更新说明/各 ABI 下载入口；清单为空时明确提示未发布。
import { ref, onMounted } from 'vue'
import { useRouter } from 'vue-router'
import { updateApi } from '@/api'

const ABIS = ['arm64-v8a', 'armeabi-v7a', 'x86_64'] as const

const router = useRouter()
const loggedIn = ref(false)
const loading = ref(false)
interface ChannelInfo { versionName: string; minVersionCode: number; changelog: string }
const stableLatest = ref<ChannelInfo | null>(null)
const specialLatest = ref<ChannelInfo | null>(null)
const stableUrls = ref<Record<string, string>>({})
const specialUrls = ref<Record<string, string>>({})

onMounted(async () => {
  // [SEC-K5] 登录态由 httpOnly Cookie 的 logged_in 标记判断（token 不再存 localStorage）
  loggedIn.value = document.cookie.split(';').some(c => c.trim().startsWith('logged_in='))
  loading.value = true
  try {
    // [TASK-UPDATE-CHANNEL] 正式版与特别版各自独立查询（versionCode=0 → 恒返回该渠道最新已发布版本）
    const [stable, special] = await Promise.all([
      loadChannel('stable'),
      loadChannel('special'),
    ])
    stableLatest.value = stable.latest
    stableUrls.value = stable.urls
    specialLatest.value = special.latest
    specialUrls.value = special.urls
  } catch {
    // 检查失败保持「未发布」呈现，不阻塞页面
  } finally {
    loading.value = false
  }
})

async function loadChannel(channel: 'stable' | 'special') {
  const urls: Record<string, string> = {}
  let latest: ChannelInfo | null = null
  let latestCode = -1
  const results = await Promise.allSettled(
    ABIS.map(abi => updateApi.check('android', abi, 0, channel)),
  )
  results.forEach((r, i) => {
    if (r.status !== 'fulfilled') return
    const d = r.value.data
    if (!d.hasUpdate || !d.url) return
    urls[ABIS[i]] = d.url
    if (d.latestVersionCode > latestCode) {
      latestCode = d.latestVersionCode
      latest = {
        versionName: d.latestVersionName,
        minVersionCode: d.minVersionCode,
        changelog: d.changelog,
      }
    }
  })
  return { latest, urls }
}

function codeToVersion(code: number) {
  const major = Math.floor(code / 10000)
  const minor = Math.floor((code % 10000) / 100)
  const patch = code % 100
  return `${major}.${minor}.${patch}`
}

function go(path: string) {
  router.push(path)
}

function download(url: string) {
  // 走浏览器直接下载（静态文件由后端托管）
  window.open(url, '_blank')
}
</script>

<style scoped>
.download-page {
  min-height: 100vh;
  background: var(--el-bg-color-page, #f5f7fa);
}

.dl-header {
  display: flex;
  align-items: center;
  justify-content: space-between;
  padding: 16px 28px;
  background: #fff;
  box-shadow: 0 1px 4px rgba(0, 0, 0, 0.06);
}

.dl-brand {
  display: flex;
  align-items: center;
  gap: 10px;
}

.dl-logo {
  font-size: 26px;
}

.dl-title {
  font-size: 18px;
  font-weight: 600;
  color: var(--el-text-color-primary);
}

.dl-main {
  max-width: 1080px;
  margin: 0 auto;
  padding: 48px 24px;
  text-align: center;
}

.dl-heading {
  font-size: 28px;
  font-weight: 700;
  color: var(--el-text-color-primary);
  margin: 0 0 8px;
}

.dl-sub {
  color: var(--el-text-color-secondary);
  margin: 0 0 36px;
}

.dl-grid {
  display: grid;
  grid-template-columns: repeat(auto-fit, minmax(260px, 1fr));
  gap: 20px;
}

.dl-card {
  border-radius: 12px;
}

.dl-card-special {
  border: 1px solid var(--el-color-warning);
}

.dl-warn {
  color: var(--el-color-warning);
  margin-top: 8px;
}

/* 未上线卡片：整体降饱和，明确"期待上线"状态 */
.dl-card-disabled {
  opacity: 0.72;
}

.dl-card-disabled .dl-icon {
  filter: grayscale(0.6);
}

.dl-icon {
  font-size: 44px;
  margin-bottom: 10px;
}

.dl-card h3 {
  margin: 0 0 8px;
  font-size: 18px;
  color: var(--el-text-color-primary);
}

.dl-desc {
  color: var(--el-text-color-regular);
  font-size: 13px;
  line-height: 1.6;
  margin: 0 0 8px;
}

.dl-meta {
  color: var(--el-text-color-placeholder);
  font-size: 12px;
  margin: 0 0 18px;
}

.dl-changelog {
  color: var(--el-text-color-regular);
  font-size: 13px;
  text-align: left;
  white-space: pre-line;
  background: var(--el-fill-color-light);
  border-radius: 6px;
  padding: 10px 12px;
  margin: 0 0 14px;
}

.dl-btn {
  width: 100%;
  margin-bottom: 8px;
}
.dl-btn-sub {
  background: var(--el-fill-color-light);
  border-color: var(--el-border-color);
  color: var(--el-text-color-regular);
}

.dl-footer {
  margin-top: 40px;
  font-size: 12px;
  color: var(--el-text-color-placeholder);
}

/* 移动端适配：单列卡片、缩小标题与边距、按钮触控区 ≥44px */
@media (max-width: 768px) {
  .dl-header {
    padding: 12px 16px;
  }

  .dl-title {
    font-size: 16px;
  }

  .dl-main {
    padding: 28px 16px;
  }

  .dl-heading {
    font-size: 22px;
  }

  .dl-grid {
    grid-template-columns: 1fr;
    gap: 14px;
  }

  .dl-btn {
    min-height: 44px;
  }
}
</style>
