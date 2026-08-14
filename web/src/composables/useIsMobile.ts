// 小趴菜 Web 3.0 — 移动端检测（<768px 视为移动端，与需求 1 断点一致）
import { ref, onMounted, onBeforeUnmount } from 'vue'

const isMobile = ref(false)

function update() {
  isMobile.value = window.innerWidth < 768
}

// 窗口 resize 时更新（横竖屏切换也覆盖）
export function useIsMobile() {
  onMounted(() => {
    update()
    window.addEventListener('resize', update)
  })
  onBeforeUnmount(() => {
    window.removeEventListener('resize', update)
  })
  return isMobile
}
