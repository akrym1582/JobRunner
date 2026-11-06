import type { AspidaClient, BasicHeaders } from 'aspida';
import { dataToURLString } from 'aspida';
import type { Methods as Methods_1v9l75v } from './api/jobrunner/operations';
import type { Methods as Methods_r98snx } from './api/jobrunner/operations/_id@string';
import type { Methods as Methods_13ckbgu } from './api/jobrunner/ui/attempts';
import type { Methods as Methods_1t484rj } from './api/jobrunner/ui/executions';
import type { Methods as Methods_1atqv8k } from './api/jobrunner/ui/graph';
import type { Methods as Methods_8zm2qn } from './api/jobrunner/ui/jobnets';
import type { Methods as Methods_vc4edf } from './api/jobrunner/ui/logs';
import type { Methods as Methods_1jm1r65 } from './api/jobrunner/ui/monthly';

const api = <T>({ baseURL, fetch }: AspidaClient<T>) => {
  const prefix = (baseURL === undefined ? '' : baseURL).replace(/\/$/, '');
  const PATH0 = '/api/jobrunner/operations';
  const PATH1 = '/api/jobrunner/ui/attempts';
  const PATH2 = '/api/jobrunner/ui/executions';
  const PATH3 = '/api/jobrunner/ui/graph';
  const PATH4 = '/api/jobrunner/ui/jobnets';
  const PATH5 = '/api/jobrunner/ui/logs';
  const PATH6 = '/api/jobrunner/ui/monthly';
  const GET = 'GET';
  const POST = 'POST';

  return {
    api: {
      jobrunner: {
        operations: {
          _id: (val3: string) => {
            const prefix3 = `${PATH0}/${val3}`;

            return {
              /**
               * @returns OK
               */
              get: (option?: { config?: T | undefined } | undefined) =>
                fetch<Methods_r98snx['get']['resBody'], BasicHeaders, Methods_r98snx['get']['status']>(prefix, prefix3, GET, option).json(),
              /**
               * @returns OK
               */
              $get: (option?: { config?: T | undefined } | undefined) =>
                fetch<Methods_r98snx['get']['resBody'], BasicHeaders, Methods_r98snx['get']['status']>(prefix, prefix3, GET, option).json().then(r => r.body),
              $path: () => `${prefix}${prefix3}`,
            };
          },
          /**
           * @returns OK
           */
          post: (option: { body: Methods_1v9l75v['post']['reqBody'], config?: T | undefined }) =>
            fetch<Methods_1v9l75v['post']['resBody'], BasicHeaders, Methods_1v9l75v['post']['status']>(prefix, PATH0, POST, option).json(),
          /**
           * @returns OK
           */
          $post: (option: { body: Methods_1v9l75v['post']['reqBody'], config?: T | undefined }) =>
            fetch<Methods_1v9l75v['post']['resBody'], BasicHeaders, Methods_1v9l75v['post']['status']>(prefix, PATH0, POST, option).json().then(r => r.body),
          $path: () => `${prefix}${PATH0}`,
        },
        ui: {
          attempts: {
            /**
             * @returns OK
             */
            get: (option: { query: Methods_13ckbgu['get']['query'], config?: T | undefined }) =>
              fetch<Methods_13ckbgu['get']['resBody'], BasicHeaders, Methods_13ckbgu['get']['status']>(prefix, PATH1, GET, option).json(),
            /**
             * @returns OK
             */
            $get: (option: { query: Methods_13ckbgu['get']['query'], config?: T | undefined }) =>
              fetch<Methods_13ckbgu['get']['resBody'], BasicHeaders, Methods_13ckbgu['get']['status']>(prefix, PATH1, GET, option).json().then(r => r.body),
            $path: (option?: { method?: 'get' | undefined; query: Methods_13ckbgu['get']['query'] } | undefined) =>
              `${prefix}${PATH1}${option && option.query ? `?${dataToURLString(option.query)}` : ''}`,
          },
          executions: {
            /**
             * @returns OK
             */
            get: (option: { query: Methods_1t484rj['get']['query'], config?: T | undefined }) =>
              fetch<Methods_1t484rj['get']['resBody'], BasicHeaders, Methods_1t484rj['get']['status']>(prefix, PATH2, GET, option).json(),
            /**
             * @returns OK
             */
            $get: (option: { query: Methods_1t484rj['get']['query'], config?: T | undefined }) =>
              fetch<Methods_1t484rj['get']['resBody'], BasicHeaders, Methods_1t484rj['get']['status']>(prefix, PATH2, GET, option).json().then(r => r.body),
            $path: (option?: { method?: 'get' | undefined; query: Methods_1t484rj['get']['query'] } | undefined) =>
              `${prefix}${PATH2}${option && option.query ? `?${dataToURLString(option.query)}` : ''}`,
          },
          graph: {
            /**
             * @returns OK
             */
            get: (option: { query: Methods_1atqv8k['get']['query'], config?: T | undefined }) =>
              fetch<Methods_1atqv8k['get']['resBody'], BasicHeaders, Methods_1atqv8k['get']['status']>(prefix, PATH3, GET, option).json(),
            /**
             * @returns OK
             */
            $get: (option: { query: Methods_1atqv8k['get']['query'], config?: T | undefined }) =>
              fetch<Methods_1atqv8k['get']['resBody'], BasicHeaders, Methods_1atqv8k['get']['status']>(prefix, PATH3, GET, option).json().then(r => r.body),
            $path: (option?: { method?: 'get' | undefined; query: Methods_1atqv8k['get']['query'] } | undefined) =>
              `${prefix}${PATH3}${option && option.query ? `?${dataToURLString(option.query)}` : ''}`,
          },
          jobnets: {
            /**
             * @returns OK
             */
            get: (option?: { config?: T | undefined } | undefined) =>
              fetch<Methods_8zm2qn['get']['resBody'], BasicHeaders, Methods_8zm2qn['get']['status']>(prefix, PATH4, GET, option).json(),
            /**
             * @returns OK
             */
            $get: (option?: { config?: T | undefined } | undefined) =>
              fetch<Methods_8zm2qn['get']['resBody'], BasicHeaders, Methods_8zm2qn['get']['status']>(prefix, PATH4, GET, option).json().then(r => r.body),
            $path: () => `${prefix}${PATH4}`,
          },
          logs: {
            /**
             * @returns OK
             */
            get: (option: { query: Methods_vc4edf['get']['query'], config?: T | undefined }) =>
              fetch<Methods_vc4edf['get']['resBody'], BasicHeaders, Methods_vc4edf['get']['status']>(prefix, PATH5, GET, option).json(),
            /**
             * @returns OK
             */
            $get: (option: { query: Methods_vc4edf['get']['query'], config?: T | undefined }) =>
              fetch<Methods_vc4edf['get']['resBody'], BasicHeaders, Methods_vc4edf['get']['status']>(prefix, PATH5, GET, option).json().then(r => r.body),
            $path: (option?: { method?: 'get' | undefined; query: Methods_vc4edf['get']['query'] } | undefined) =>
              `${prefix}${PATH5}${option && option.query ? `?${dataToURLString(option.query)}` : ''}`,
          },
          monthly: {
            /**
             * @returns OK
             */
            get: (option: { query: Methods_1jm1r65['get']['query'], config?: T | undefined }) =>
              fetch<Methods_1jm1r65['get']['resBody'], BasicHeaders, Methods_1jm1r65['get']['status']>(prefix, PATH6, GET, option).json(),
            /**
             * @returns OK
             */
            $get: (option: { query: Methods_1jm1r65['get']['query'], config?: T | undefined }) =>
              fetch<Methods_1jm1r65['get']['resBody'], BasicHeaders, Methods_1jm1r65['get']['status']>(prefix, PATH6, GET, option).json().then(r => r.body),
            $path: (option?: { method?: 'get' | undefined; query: Methods_1jm1r65['get']['query'] } | undefined) =>
              `${prefix}${PATH6}${option && option.query ? `?${dataToURLString(option.query)}` : ''}`,
          },
        },
      },
    },
  };
};

export type ApiInstance = ReturnType<typeof api>;
export default api;
