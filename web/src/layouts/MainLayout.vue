<script setup lang="ts">
// 小趴菜 Web 3.0 — 主布局（侧边栏 + 顶栏 + 内容区）
import { ref, computed, onMounted } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { useAuthStore } from '@/stores/auth'
import { useUiStore } from '@/stores/ui'
import {
  Odometer, Monitor, Setting, Notification, DataAnalysis, Tools,
  UserFilled, DocumentChecked, SetUp, FolderOpened, FirstAidKit, Connection,
  Expand, Fold, Moon, Sunny, SwitchButton,
} from '@element-plus/icons-vue'

const route = useRoute()
const router = useRouter()
const auth = useAuthStore()
const ui = useUiStore()

// 菜单项
interface MenuItem {
  path: string
  title: string
  icon: any
  role?: string
}

const userMenuItems: MenuItem[] = [
  { path: '/dashboard', title: '仪表盘', icon: Odometer },
  { path: '/devices', title: '设备管理', icon: Monitor },
  { path: '/policies', title: '策略配置', icon: Setting },
  { path: '/announcements', title: '公告管理', icon: Notification },
  { path: '/reports', title: '使用报告', icon: DataAnalysis },
  { path: '/settings', title: '设置', icon: Tools },
]

const adminMenuItems: MenuItem[] = [
  { path: '/admin/accounts', title: '账号管理', icon: UserFilled, role: 'admin' },
  { path: '/admin/devices', title: '设备管理', icon: Monitor, role: 'admin' },
  { path: '/admin/audit', title: '审计日志', icon: DocumentChecked, role: 'admin' },
  { path: '/admin/system', title: '系统设置', icon: SetUp, role: 'admin' },
  { path: '/admin/data', title: '数据管理', icon: FolderOpened, role: 'admin' },
  { path: '/admin/diagnostics', title: '故障诊断', icon: FirstAidKit, role: 'admin' },
  { path: '/admin/relay-sessions', title: '云端中继', icon: Connection, role: 'admin' },
]

const isAdmin = computed(() => auth.isAdmin)

// 默认展开管理菜单
const adminMenuOpen = ref(true)

function navigateTo(path: string) {
  router.push(path)
}

async function handleLogout() {
  await auth.logout()
}

// 初始化：恢复会话
onMounted(async () => {
  if (!auth.isAuthenticated) {
    const ok = await auth.restoreSession()
    if (!ok) {
      router.push('/login')
    }
  }
})
</script>

<template>
  <el-container class="main-layout">
    <!-- 侧边栏 -->
    <el-aside :width="ui.sidebarCollapsed ? '64px' : '220px'" class="layout-aside">
      <div class="aside-header">
        <span v-if="!ui.sidebarCollapsed" class="aside-logo">
          <span class="logo-icon">🛡️</span>
          <span class="logo-text">小趴菜</span>
        </span>
        <span v-else class="aside-logo-collapsed">🛡️</span>
      </div>

      <el-menu
        :default-active="route.path"
        :collapse="ui.sidebarCollapsed"
        :collapse-transition="false"
        background-color="transparent"
        text-color="var(--el-menu-text-color)"
        active-text-color="var(--el-color-primary)"
        class="aside-menu"
      >
        <!-- 用户菜单 -->
        <el-menu-item
          v-for="item in userMenuItems"
          :key="item.path"
          :index="item.path"
          @click="navigateTo(item.path)"
        >
          <el-icon><component :is="item.icon" /></el-icon>
          <template #title>{{ item.title }}</template>
        </el-menu-item>

        <!-- 管理菜单（仅 admin） -->
        <template v-if="isAdmin">
          <el-divider style="margin: 8px 0" />
          <el-sub-menu index="admin-group" v-model:open="adminMenuOpen">
            <template #title>
              <el-icon><SetUp /></el-icon>
              <span>管理后台</span>
            </template>
            <el-menu-item
              v-for="item in adminMenuItems"
              :key="item.path"
              :index="item.path"
              @click="navigateTo(item.path)"
            >
              <el-icon><component :is="item.icon" /></el-icon>
              <template #title>{{ item.title }}</template>
            </el-menu-item>
          </el-sub-menu>
        </template>
      </el-menu>

      <!-- 底部控制 -->
      <div class="aside-footer">
        <el-button
          :icon="ui.sidebarCollapsed ? Expand : Fold"
          text
          @click="ui.toggleSidebar()"
          style="width: 100%"
        />
      </div>
    </el-aside>

    <!-- 主内容区 -->
    <el-container class="layout-main">
      <!-- 顶栏 -->
      <el-header class="layout-header" height="56px">
        <div class="header-left">
          <el-breadcrumb separator="/">
            <el-breadcrumb-item :to="{ path: '/' }">首页</el-breadcrumb-item>
            <el-breadcrumb-item v-if="route.meta.title">{{ route.meta.title }}</el-breadcrumb-item>
          </el-breadcrumb>
        </div>
        <div class="header-right">
          <el-switch
            v-model="ui.darkMode"
            :active-icon="Moon"
            :inactive-icon="Sunny"
            inline-prompt
            @change="ui.toggleDarkMode()"
          />
          <el-dropdown trigger="click">
            <span class="user-avatar">
              <el-avatar :size="32" icon="UserFilled" />
              <span class="username">{{ auth.user?.displayName || auth.user?.username || '用户' }}</span>
            </span>
            <template #dropdown>
              <el-dropdown-menu>
                <el-dropdown-item>
                  <span>角色：{{ isAdmin ? '管理员' : '家长' }}</span>
                </el-dropdown-item>
                <el-dropdown-item divided @click="handleLogout">
                  <el-icon><SwitchButton /></el-icon> 退出登录
                </el-dropdown-item>
              </el-dropdown-menu>
            </template>
          </el-dropdown>
        </div>
      </el-header>

      <!-- 内容区 -->
      <el-main class="layout-content">
        <router-view />
      </el-main>
    </el-container>
  </el-container>
</template>

<style scoped>
.main-layout {
  height: 100vh;
  overflow: hidden;
}

.layout-aside {
  background: var(--el-bg-color);
  border-right: 1px solid var(--el-border-color-light);
  display: flex;
  flex-direction: column;
  transition: width 0.3s;
  overflow: hidden;
}

.aside-header {
  height: 56px;
  display: flex;
  align-items: center;
  justify-content: center;
  border-bottom: 1px solid var(--el-border-color-lighter);
  flex-shrink: 0;
}

.aside-logo {
  display: flex;
  align-items: center;
  gap: 8px;
  font-size: 18px;
  font-weight: 700;
  white-space: nowrap;
  color: var(--el-color-primary);
}

.aside-logo-collapsed {
  font-size: 24px;
}

.aside-menu {
  flex: 1;
  overflow-y: auto;
  border-right: none;
}

.aside-menu .el-divider {
  margin: 4px 0;
}

.aside-footer {
  border-top: 1px solid var(--el-border-color-lighter);
  padding: 4px;
  flex-shrink: 0;
}

.layout-main {
  flex-direction: column;
  overflow: hidden;
}

.layout-header {
  display: flex;
  align-items: center;
  justify-content: space-between;
  border-bottom: 1px solid var(--el-border-color-light);
  background: var(--el-bg-color);
  padding: 0 20px;
  flex-shrink: 0;
}

.header-left {
  display: flex;
  align-items: center;
}

.header-right {
  display: flex;
  align-items: center;
  gap: 16px;
}

.user-avatar {
  display: flex;
  align-items: center;
  gap: 8px;
  cursor: pointer;
}

.username {
  font-size: 14px;
  color: var(--el-text-color-regular);
}

.layout-content {
  flex: 1;
  overflow-y: auto;
  padding: 20px;
  background: var(--el-bg-color-page);
}
</style>
