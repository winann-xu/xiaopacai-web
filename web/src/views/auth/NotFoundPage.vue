<script setup lang="ts">
// 小趴菜 Web 3.0 — 404 页面
import { useRouter } from 'vue-router'

const router = useRouter()

function goHome() {
  router.push('/dashboard')
}
</script>

<template>
  <div class="not-found-page">
    <div class="nf-content">
      <h1 class="nf-code">404</h1>
      <p class="nf-text">页面未找到</p>
      <p class="nf-hint">您访问的页面不存在或已被移除</p>
      <el-button type="primary" size="large" @click="goHome">返回首页</el-button>
    </div>
  </div>
</template>

<style scoped>
.not-found-page {
  min-height: 100vh;
  display: flex;
  align-items: center;
  justify-content: center;
  background: var(--el-bg-color-page);
}

.nf-content {
  text-align: center;
}

.nf-code {
  font-size: 120px;
  font-weight: 800;
  color: var(--el-color-primary);
  margin: 0;
  line-height: 1;
  opacity: 0.6;
}

.nf-text {
  font-size: 24px;
  color: var(--el-text-color-primary);
  margin: 16px 0 8px;
}

.nf-hint {
  font-size: 14px;
  color: var(--el-text-color-secondary);
  margin: 0 0 32px;
}
</style>
