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
    },
});
