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
      <p class="dl-sub">小趴菜守护 · 家长监控客户端（Android 陆续上线更多平台）</p>

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
        <span>小趴菜 · 开源免费 · 安全守护</span>
      </footer>
    </main>
  </div>
</template>

<script setup lang="ts">
import { ref, onMounted } from 'vue'
import { useRouter } from 'vue-router'
import { updateApi } from '@/api'

const ABIS = ['arm64-v8a', 'armeabi-v7a', 'x86_64'] as const

const router = useRouter()
const loggedIn = ref(false)
const loading = ref(false)
interface ChannelInfo { versionName: string; minVersionCode: number; changelog: string }
const stableLatest = ref<ChannelInfo | null>(null)
const stableUrls = ref<Record<string, string>>({})

onMounted(async () => {
  loggedIn.value = document.cookie.split(';').some(c => c.trim().startsWith('logged_in='))
  loading.value = true
  try {
    const stable = await loadChannel('stable')
    stableLatest.value = stable.latest
    stableUrls.value = stable.urls
  } catch {
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
