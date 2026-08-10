<script setup lang="ts">
// 小趴菜 Web 3.0 首页 — P1 骨架（P3 实现完整登录与导航）
</script>

<template>
  <div class="home-page">
    <h1>🏠 小趴菜 Web 3.0</h1>
    <p>儿童守护 · 家长 Web 界面</p>
    <p class="version">Version 3.0.0-p1</p>
    <div class="links">
      <router-link to="/dashboard">仪表盘</router-link>
      <router-link to="/devices">设备管理</router-link>
      <router-link to="/policies">策略配置</router-link>
      <router-link to="/announcements">公告管理</router-link>
      <router-link to="/reports">使用报告</router-link>
      <router-link to="/settings">设置</router-link>
    </div>
    <div class="links admin-links">
      <router-link to="/admin/accounts">管理后台</router-link>
    </div>
  </div>
</template>

<style scoped>
.home-page {
  display: flex;
  flex-direction: column;
  align-items: center;
  justify-content: center;
  min-height: 100vh;
  gap: 12px;
  text-align: center;
}
.version {
  color: #999;
  font-size: 14px;
}
.links {
  display: flex;
  flex-wrap: wrap;
  gap: 12px;
  margin-top: 16px;
}
.links a {
  padding: 8px 20px;
  background: #409EFF;
  color: #fff;
  border-radius: 6px;
  text-decoration: none;
}
.admin-links a {
  background: #4CAF50;
}
</style>
