// Node ESM 解析钩子：reze 的 tsc 产物用无扩展名相对导入（`from "./math"`），
// 而 ESM 要求显式扩展名。这里只在"目标文件确实存在"时补 .js，不修改 oracle 源码本身。
import { existsSync } from "node:fs"
import { fileURLToPath } from "node:url"

export async function resolve(specifier, context, nextResolve) {
    if ((specifier.startsWith("./") || specifier.startsWith("../")) && !/\.[a-z]+$/i.test(specifier)) {
        if (context.parentURL) {
            const candidate = new URL(specifier + ".js", context.parentURL)
            if (existsSync(fileURLToPath(candidate))) {
                return nextResolve(specifier + ".js", context)
            }
        }
    }
    return nextResolve(specifier, context)
}
