export default {
  async fetch(request, env, ctx) {
    // 获取请求的 URL
    const url = new URL(request.url);
    let path = url.pathname;

    // 处理根路径
    if (path === '/') {
      path = '/metadata.xml';
    }

    // 移除开头的斜杠
    if (path.startsWith('/')) {
      path = path.slice(1);
    }

    try {
      // 从 KV 或静态资源获取文件
      // 注意：Cloudflare Workers Sites 会自动处理静态文件
      return await env.ASSETS.fetch(request);
    } catch (e) {
      return new Response('Not Found', { status: 404 });
    }
  },
};
