// 小趴菜 Web 3.0 — UI 状态管理 (Pinia)
import { defineStore } from 'pinia'
import { ref, watch } from 'vue'

export const useUiStore = defineStore('ui', () => {
  // ---- state ----
  const sidebarCollapsed = ref(false)
  const darkMode = ref(localStorage.getItem('dark_mode') === 'true')
  const locale = ref<'zh-CN' | 'en'>('zh-CN')

  // 持久化深色模式
  watch(darkMode, (val) => {
    localStorage.setItem('dark_mode', String(val))
    if (val) {
      document.documentElement.classList.add('dark')
    } else {
      document.documentElement.classList.remove('dark')
    }
  }, { immediate: true })

  // ---- actions ----
  function toggleSidebar() {
    sidebarCollapsed.value = !sidebarCollapsed.value
  }

  function toggleDarkMode() {
    darkMode.value = !darkMode.value
  }

  return {
    sidebarCollapsed, darkMode, locale,
    toggleSidebar, toggleDarkMode,
  }
})
