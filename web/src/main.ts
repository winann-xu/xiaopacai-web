// 小趴菜 Web 3.0 — Vue 3 应用入口
import { createApp } from 'vue'
import { createPinia } from 'pinia'
import ElementPlus from 'element-plus'
import 'element-plus/dist/index.css'
// 深色模式样式
import 'element-plus/theme-chalk/dark/css-vars.css'
// 中文本地化
import zhCn from 'element-plus/dist/locale/zh-cn.mjs'
import App from './App.vue'
import router from './router'

const app = createApp(App)

// 状态管理
app.use(createPinia())

// 路由
app.use(router)

// UI 框架（Element Plus 中文 + 深色模式支持）
app.use(ElementPlus, {
  locale: zhCn,
})

app.mount('#app')
