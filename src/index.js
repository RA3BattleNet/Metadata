/**
 * Cloudflare Pages / Workers 静态资源透传。
 * 业务数据入口为 /metadata.xml（见 _redirects：/ -> /metadata.xml）。
 */
export default {
  async fetch(request, env) {
    return env.ASSETS.fetch(request);
  },
};
