<script setup lang="ts">
// 小趴菜 Web 3.0 — 根组件（P1 骨架）
// P3 阶段实现：登录状态守卫 + 侧边栏布局 + 深色模式切换
</script>

<template>
  <div id="xiaopacai-app">
    <router-view />
  </div>
</template>

<style>
/* 全局样式重置 */
* {
  margin: 0;
  padding: 0;
  box-sizing: border-box;
}

html, body {
  height: 100%;
  font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto,
               'Helvetica Neue', Arial, 'Noto Sans SC', sans-serif;
  -webkit-font-smoothing: antialiased;
  -moz-osx-font-smoothing: grayscale;
}

#xiaopacai-app {
  height: 100%;
}
</style>
