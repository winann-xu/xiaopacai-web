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

/* ===== 全局移动端适配（<768px，需求 1）===== */
@media (max-width: 768px) {
  /* 弹窗自适应宽度，避免横向溢出 */
  .el-dialog {
    width: 92% !important;
    max-width: 92vw;
    margin-top: 8vh !important;
  }

  /* 表单控件全宽 */
  .el-dialog .el-form-item__content {
    flex-wrap: wrap;
  }

  /* 触控友好：按钮/开关最小触控区约 44px */
  .el-button {
    min-height: 36px;
  }

  .el-dialog .el-button,
  .el-card .el-button {
    min-height: 44px;
  }

  /* 移动端隐藏表格横向滚动兜底，页面提供卡片式降级 */
  .el-table--fit {
    overflow-x: auto;
  }

  /* 管理端页头移动端堆叠 */
  .admin-page .page-header {
    flex-direction: column;
    align-items: stretch;
    gap: 10px;
  }

  .admin-page .page-actions {
    flex-wrap: wrap;
  }

  .admin-page .page-actions .el-button {
    min-height: 44px;
  }

  /* 抽屉/弹窗内表单控件触控友好 */
  .el-drawer .el-form-item__content {
    flex-wrap: wrap;
  }
}
</style>
