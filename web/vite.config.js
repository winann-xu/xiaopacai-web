import { defineConfig } from 'vite';
import vue from '@vitejs/plugin-vue';
import { resolve } from 'path';
// https://vitejs.dev/config/
export default defineConfig({
    plugins: [vue()],
    resolve: {
        alias: {
            '@': resolve(__dirname, 'src'),
        },
    },
    server: {
        port: 5173,
        proxy: {
            // 开发环境代理后端 API 到 ASP.NET Core
            '/api': {
                target: 'http://127.0.0.1:5000',
                changeOrigin: true,
            },
            '/hubs': {
                target: 'http://127.0.0.1:5000',
                changeOrigin: true,
                ws: true, // SignalR WebSocket
            },
        },
    },
    build: {
        outDir: 'dist',
        assetsDir: 'assets',
        sourcemap: false,
        rollupOptions: {
            output: {
                // 分包优化：将大型第三方库拆分为独立 chunk，减小首屏体积
                manualChunks: function (id) {
                    // Element Plus UI 组件库 (~1MB)
                    if (id.includes('node_modules/element-plus')) {
                        return 'vendor-element-plus';
                    }
                    // ECharts 图表库 (~500KB)
                    if (id.includes('node_modules/echarts') || id.includes('node_modules/vue-echarts') || id.includes('node_modules/zrender')) {
                        return 'vendor-echarts';
                    }
                    // Vue 核心生态（vue/vue-router/pinia）
                    if (id.includes('node_modules/vue') || id.includes('node_modules/@vue') || id.includes('node_modules/pinia') || id.includes('node_modules/vue-router')) {
                        return 'vendor-vue';
                    }
                    // 通用工具库（axios/dayjs/@microsoft/signalr）
                    if (id.includes('node_modules/axios') || id.includes('node_modules/dayjs') || id.includes('node_modules/@microsoft/signalr')) {
                        return 'vendor-utils';
                    }
                    // 其他 node_modules 归入 vendors
                    if (id.includes('node_modules')) {
                        return 'vendors';
                    }
                },
            },
        },
    },
});
