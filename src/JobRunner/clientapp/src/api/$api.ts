import type { AspidaClient, BasicHeaders } from 'aspida';
import { dataToURLString } from 'aspida';
import type { Methods as Methods_1v9l75v } from './api/jobrunner/operations';
import type { Methods as Methods_r98snx } from './api/jobrunner/operations/_id@string';
import type { Methods as Methods_13ckbgu } from './api/jobrunner/ui/attempts';
import type { Methods as Methods_1ky6f4t } from './api/jobrunner/ui/definition-editor';
import type { Methods as Methods_1qh6dqw } from './api/jobrunner/ui/definition-editor/calendar';
import type { Methods as Methods_8y1mik } from './api/jobrunner/ui/definition-editor/export';
import type { Methods as Methods_1r0i1hl } from './api/jobrunner/ui/definition-editor/groups/export';
import type { Methods as Methods_1y8qxgq } from './api/jobrunner/ui/definition-editor/groups/import';
import type { Methods as Methods_i7i0m5 } from './api/jobrunner/ui/definition-editor/local';
import type { Methods as Methods_s87h28 } from './api/jobrunner/ui/definition-editor/local/new';
import type { Methods as Methods_nmo6j5 } from './api/jobrunner/ui/definition-editor/paste';
import type { Methods as Methods_131cyve } from './api/jobrunner/ui/definition-editor/preview';
import type { Methods as Methods_myrzod } from './api/jobrunner/ui/definition-editor/publication-preview';
import type { Methods as Methods_13lui05 } from './api/jobrunner/ui/definition-editor/publish';
import type { Methods as Methods_y20aq8 } from './api/jobrunner/ui/definition-editor/schedules';
import type { Methods as Methods_598igr } from './api/jobrunner/ui/definition-editor/shared';
import type { Methods as Methods_1t484rj } from './api/jobrunner/ui/executions';
import type { Methods as Methods_1atqv8k } from './api/jobrunner/ui/graph';
import type { Methods as Methods_cqn9ps } from './api/jobrunner/ui/groups';
import type { Methods as Methods_8zm2qn } from './api/jobrunner/ui/jobnets';
import type { Methods as Methods_vc4edf } from './api/jobrunner/ui/logs';
import type { Methods as Methods_1jm1r65 } from './api/jobrunner/ui/monthly';
import type { Methods as Methods_1cgpyyc } from './api/jobrunner/ui/runtime';

const api = <T>({ baseURL, fetch }: AspidaClient<T>) => {
  const prefix = (baseURL === undefined ? '' : baseURL).replace(/\/$/, '');
  const PATH0 = '/api/jobrunner/operations';
  const PATH1 = '/api/jobrunner/ui/attempts';
  const PATH2 = '/api/jobrunner/ui/definition-editor';
  const PATH3 = '/api/jobrunner/ui/definition-editor/calendar';
  const PATH4 = '/api/jobrunner/ui/definition-editor/export';
  const PATH5 = '/api/jobrunner/ui/definition-editor/groups/export';
  const PATH6 = '/api/jobrunner/ui/definition-editor/groups/import';
  const PATH7 = '/api/jobrunner/ui/definition-editor/local';
  const PATH8 = '/api/jobrunner/ui/definition-editor/local/new';
  const PATH9 = '/api/jobrunner/ui/definition-editor/paste';
  const PATH10 = '/api/jobrunner/ui/definition-editor/preview';
  const PATH11 = '/api/jobrunner/ui/definition-editor/publication-preview';
  const PATH12 = '/api/jobrunner/ui/definition-editor/publish';
  const PATH13 = '/api/jobrunner/ui/definition-editor/schedules';
  const PATH14 = '/api/jobrunner/ui/definition-editor/shared';
  const PATH15 = '/api/jobrunner/ui/executions';
  const PATH16 = '/api/jobrunner/ui/graph';
  const PATH17 = '/api/jobrunner/ui/groups';
  const PATH18 = '/api/jobrunner/ui/jobnets';
  const PATH19 = '/api/jobrunner/ui/logs';
  const PATH20 = '/api/jobrunner/ui/monthly';
  const PATH21 = '/api/jobrunner/ui/runtime';
  const GET = 'GET';
  const POST = 'POST';
  const PUT = 'PUT';

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
          definition_editor: {
            calendar: {
              /**
               * @returns OK
               */
              post: (option: { body: Methods_1qh6dqw['post']['reqBody'], config?: T | undefined }) =>
                fetch<Methods_1qh6dqw['post']['resBody'], BasicHeaders, Methods_1qh6dqw['post']['status']>(prefix, PATH3, POST, option).json(),
              /**
               * @returns OK
               */
              $post: (option: { body: Methods_1qh6dqw['post']['reqBody'], config?: T | undefined }) =>
                fetch<Methods_1qh6dqw['post']['resBody'], BasicHeaders, Methods_1qh6dqw['post']['status']>(prefix, PATH3, POST, option).json().then(r => r.body),
              $path: () => `${prefix}${PATH3}`,
            },
            export: {
              /**
               * @returns OK
               */
              post: (option: { body: Methods_8y1mik['post']['reqBody'], config?: T | undefined }) =>
                fetch<Methods_8y1mik['post']['resBody'], BasicHeaders, Methods_8y1mik['post']['status']>(prefix, PATH4, POST, option).blob(),
              /**
               * @returns OK
               */
              $post: (option: { body: Methods_8y1mik['post']['reqBody'], config?: T | undefined }) =>
                fetch<Methods_8y1mik['post']['resBody'], BasicHeaders, Methods_8y1mik['post']['status']>(prefix, PATH4, POST, option).blob().then(r => r.body),
              $path: () => `${prefix}${PATH4}`,
            },
            groups: {
              export: {
                /**
                 * @returns OK
                 */
                post: (option: { body: Methods_1r0i1hl['post']['reqBody'], config?: T | undefined }) =>
                  fetch<Methods_1r0i1hl['post']['resBody'], BasicHeaders, Methods_1r0i1hl['post']['status']>(prefix, PATH5, POST, option).blob(),
                /**
                 * @returns OK
                 */
                $post: (option: { body: Methods_1r0i1hl['post']['reqBody'], config?: T | undefined }) =>
                  fetch<Methods_1r0i1hl['post']['resBody'], BasicHeaders, Methods_1r0i1hl['post']['status']>(prefix, PATH5, POST, option).blob().then(r => r.body),
                $path: () => `${prefix}${PATH5}`,
              },
              import: {
                /**
                 * @returns OK
                 */
                post: (option: { body: Methods_1y8qxgq['post']['reqBody'], config?: T | undefined }) =>
                  fetch<Methods_1y8qxgq['post']['resBody'], BasicHeaders, Methods_1y8qxgq['post']['status']>(prefix, PATH6, POST, option).json(),
                /**
                 * @returns OK
                 */
                $post: (option: { body: Methods_1y8qxgq['post']['reqBody'], config?: T | undefined }) =>
                  fetch<Methods_1y8qxgq['post']['resBody'], BasicHeaders, Methods_1y8qxgq['post']['status']>(prefix, PATH6, POST, option).json().then(r => r.body),
                $path: () => `${prefix}${PATH6}`,
              },
            },
            local: {
              new: {
                /**
                 * @returns OK
                 */
                post: (option: { body: Methods_s87h28['post']['reqBody'], config?: T | undefined }) =>
                  fetch<Methods_s87h28['post']['resBody'], BasicHeaders, Methods_s87h28['post']['status']>(prefix, PATH8, POST, option).json(),
                /**
                 * @returns OK
                 */
                $post: (option: { body: Methods_s87h28['post']['reqBody'], config?: T | undefined }) =>
                  fetch<Methods_s87h28['post']['resBody'], BasicHeaders, Methods_s87h28['post']['status']>(prefix, PATH8, POST, option).json().then(r => r.body),
                $path: () => `${prefix}${PATH8}`,
              },
              /**
               * @returns OK
               */
              post: (option: { body: Methods_i7i0m5['post']['reqBody'], config?: T | undefined }) =>
                fetch<Methods_i7i0m5['post']['resBody'], BasicHeaders, Methods_i7i0m5['post']['status']>(prefix, PATH7, POST, option).json(),
              /**
               * @returns OK
               */
              $post: (option: { body: Methods_i7i0m5['post']['reqBody'], config?: T | undefined }) =>
                fetch<Methods_i7i0m5['post']['resBody'], BasicHeaders, Methods_i7i0m5['post']['status']>(prefix, PATH7, POST, option).json().then(r => r.body),
              $path: () => `${prefix}${PATH7}`,
            },
            paste: {
              /**
               * @returns OK
               */
              post: (option: { body: Methods_nmo6j5['post']['reqBody'], config?: T | undefined }) =>
                fetch<Methods_nmo6j5['post']['resBody'], BasicHeaders, Methods_nmo6j5['post']['status']>(prefix, PATH9, POST, option).json(),
              /**
               * @returns OK
               */
              $post: (option: { body: Methods_nmo6j5['post']['reqBody'], config?: T | undefined }) =>
                fetch<Methods_nmo6j5['post']['resBody'], BasicHeaders, Methods_nmo6j5['post']['status']>(prefix, PATH9, POST, option).json().then(r => r.body),
              $path: () => `${prefix}${PATH9}`,
            },
            preview: {
              /**
               * @returns OK
               */
              post: (option: { body: Methods_131cyve['post']['reqBody'], config?: T | undefined }) =>
                fetch<Methods_131cyve['post']['resBody'], BasicHeaders, Methods_131cyve['post']['status']>(prefix, PATH10, POST, option).json(),
              /**
               * @returns OK
               */
              $post: (option: { body: Methods_131cyve['post']['reqBody'], config?: T | undefined }) =>
                fetch<Methods_131cyve['post']['resBody'], BasicHeaders, Methods_131cyve['post']['status']>(prefix, PATH10, POST, option).json().then(r => r.body),
              $path: () => `${prefix}${PATH10}`,
            },
            publication_preview: {
              /**
               * @returns OK
               */
              post: (option: { body: Methods_myrzod['post']['reqBody'], config?: T | undefined }) =>
                fetch<Methods_myrzod['post']['resBody'], BasicHeaders, Methods_myrzod['post']['status']>(prefix, PATH11, POST, option).json(),
              /**
               * @returns OK
               */
              $post: (option: { body: Methods_myrzod['post']['reqBody'], config?: T | undefined }) =>
                fetch<Methods_myrzod['post']['resBody'], BasicHeaders, Methods_myrzod['post']['status']>(prefix, PATH11, POST, option).json().then(r => r.body),
              $path: () => `${prefix}${PATH11}`,
            },
            publish: {
              /**
               * @returns OK
               */
              post: (option: { body: Methods_13lui05['post']['reqBody'], config?: T | undefined }) =>
                fetch<Methods_13lui05['post']['resBody'], BasicHeaders, Methods_13lui05['post']['status']>(prefix, PATH12, POST, option).json(),
              /**
               * @returns OK
               */
              $post: (option: { body: Methods_13lui05['post']['reqBody'], config?: T | undefined }) =>
                fetch<Methods_13lui05['post']['resBody'], BasicHeaders, Methods_13lui05['post']['status']>(prefix, PATH12, POST, option).json().then(r => r.body),
              $path: () => `${prefix}${PATH12}`,
            },
            schedules: {
              /**
               * @returns OK
               */
              post: (option: { body: Methods_y20aq8['post']['reqBody'], config?: T | undefined }) =>
                fetch<Methods_y20aq8['post']['resBody'], BasicHeaders, Methods_y20aq8['post']['status']>(prefix, PATH13, POST, option).json(),
              /**
               * @returns OK
               */
              $post: (option: { body: Methods_y20aq8['post']['reqBody'], config?: T | undefined }) =>
                fetch<Methods_y20aq8['post']['resBody'], BasicHeaders, Methods_y20aq8['post']['status']>(prefix, PATH13, POST, option).json().then(r => r.body),
              $path: () => `${prefix}${PATH13}`,
            },
            shared: {
              /**
               * @returns OK
               */
              get: (option?: { config?: T | undefined } | undefined) =>
                fetch<Methods_598igr['get']['resBody'], BasicHeaders, Methods_598igr['get']['status']>(prefix, PATH14, GET, option).json(),
              /**
               * @returns OK
               */
              $get: (option?: { config?: T | undefined } | undefined) =>
                fetch<Methods_598igr['get']['resBody'], BasicHeaders, Methods_598igr['get']['status']>(prefix, PATH14, GET, option).json().then(r => r.body),
              /**
               * @returns OK
               */
              put: (option: { body: Methods_598igr['put']['reqBody'], config?: T | undefined }) =>
                fetch<Methods_598igr['put']['resBody'], BasicHeaders, Methods_598igr['put']['status']>(prefix, PATH14, PUT, option).json(),
              /**
               * @returns OK
               */
              $put: (option: { body: Methods_598igr['put']['reqBody'], config?: T | undefined }) =>
                fetch<Methods_598igr['put']['resBody'], BasicHeaders, Methods_598igr['put']['status']>(prefix, PATH14, PUT, option).json().then(r => r.body),
              $path: () => `${prefix}${PATH14}`,
            },
            /**
             * @returns OK
             */
            get: (option?: { query?: Methods_1ky6f4t['get']['query'] | undefined, config?: T | undefined } | undefined) =>
              fetch<Methods_1ky6f4t['get']['resBody'], BasicHeaders, Methods_1ky6f4t['get']['status']>(prefix, PATH2, GET, option).json(),
            /**
             * @returns OK
             */
            $get: (option?: { query?: Methods_1ky6f4t['get']['query'] | undefined, config?: T | undefined } | undefined) =>
              fetch<Methods_1ky6f4t['get']['resBody'], BasicHeaders, Methods_1ky6f4t['get']['status']>(prefix, PATH2, GET, option).json().then(r => r.body),
            $path: (option?: { method?: 'get' | undefined; query: Methods_1ky6f4t['get']['query'] } | undefined) =>
              `${prefix}${PATH2}${option && option.query ? `?${dataToURLString(option.query)}` : ''}`,
          },
          executions: {
            /**
             * @returns OK
             */
            get: (option: { query: Methods_1t484rj['get']['query'], config?: T | undefined }) =>
              fetch<Methods_1t484rj['get']['resBody'], BasicHeaders, Methods_1t484rj['get']['status']>(prefix, PATH15, GET, option).json(),
            /**
             * @returns OK
             */
            $get: (option: { query: Methods_1t484rj['get']['query'], config?: T | undefined }) =>
              fetch<Methods_1t484rj['get']['resBody'], BasicHeaders, Methods_1t484rj['get']['status']>(prefix, PATH15, GET, option).json().then(r => r.body),
            $path: (option?: { method?: 'get' | undefined; query: Methods_1t484rj['get']['query'] } | undefined) =>
              `${prefix}${PATH15}${option && option.query ? `?${dataToURLString(option.query)}` : ''}`,
          },
          graph: {
            /**
             * @returns OK
             */
            get: (option: { query: Methods_1atqv8k['get']['query'], config?: T | undefined }) =>
              fetch<Methods_1atqv8k['get']['resBody'], BasicHeaders, Methods_1atqv8k['get']['status']>(prefix, PATH16, GET, option).json(),
            /**
             * @returns OK
             */
            $get: (option: { query: Methods_1atqv8k['get']['query'], config?: T | undefined }) =>
              fetch<Methods_1atqv8k['get']['resBody'], BasicHeaders, Methods_1atqv8k['get']['status']>(prefix, PATH16, GET, option).json().then(r => r.body),
            $path: (option?: { method?: 'get' | undefined; query: Methods_1atqv8k['get']['query'] } | undefined) =>
              `${prefix}${PATH16}${option && option.query ? `?${dataToURLString(option.query)}` : ''}`,
          },
          groups: {
            /**
             * @returns OK
             */
            get: (option?: { config?: T | undefined } | undefined) =>
              fetch<Methods_cqn9ps['get']['resBody'], BasicHeaders, Methods_cqn9ps['get']['status']>(prefix, PATH17, GET, option).json(),
            /**
             * @returns OK
             */
            $get: (option?: { config?: T | undefined } | undefined) =>
              fetch<Methods_cqn9ps['get']['resBody'], BasicHeaders, Methods_cqn9ps['get']['status']>(prefix, PATH17, GET, option).json().then(r => r.body),
            $path: () => `${prefix}${PATH17}`,
          },
          jobnets: {
            /**
             * @returns OK
             */
            get: (option?: { config?: T | undefined } | undefined) =>
              fetch<Methods_8zm2qn['get']['resBody'], BasicHeaders, Methods_8zm2qn['get']['status']>(prefix, PATH18, GET, option).json(),
            /**
             * @returns OK
             */
            $get: (option?: { config?: T | undefined } | undefined) =>
              fetch<Methods_8zm2qn['get']['resBody'], BasicHeaders, Methods_8zm2qn['get']['status']>(prefix, PATH18, GET, option).json().then(r => r.body),
            $path: () => `${prefix}${PATH18}`,
          },
          logs: {
            /**
             * @returns OK
             */
            get: (option: { query: Methods_vc4edf['get']['query'], config?: T | undefined }) =>
              fetch<Methods_vc4edf['get']['resBody'], BasicHeaders, Methods_vc4edf['get']['status']>(prefix, PATH19, GET, option).json(),
            /**
             * @returns OK
             */
            $get: (option: { query: Methods_vc4edf['get']['query'], config?: T | undefined }) =>
              fetch<Methods_vc4edf['get']['resBody'], BasicHeaders, Methods_vc4edf['get']['status']>(prefix, PATH19, GET, option).json().then(r => r.body),
            $path: (option?: { method?: 'get' | undefined; query: Methods_vc4edf['get']['query'] } | undefined) =>
              `${prefix}${PATH19}${option && option.query ? `?${dataToURLString(option.query)}` : ''}`,
          },
          monthly: {
            /**
             * @returns OK
             */
            get: (option: { query: Methods_1jm1r65['get']['query'], config?: T | undefined }) =>
              fetch<Methods_1jm1r65['get']['resBody'], BasicHeaders, Methods_1jm1r65['get']['status']>(prefix, PATH20, GET, option).json(),
            /**
             * @returns OK
             */
            $get: (option: { query: Methods_1jm1r65['get']['query'], config?: T | undefined }) =>
              fetch<Methods_1jm1r65['get']['resBody'], BasicHeaders, Methods_1jm1r65['get']['status']>(prefix, PATH20, GET, option).json().then(r => r.body),
            $path: (option?: { method?: 'get' | undefined; query: Methods_1jm1r65['get']['query'] } | undefined) =>
              `${prefix}${PATH20}${option && option.query ? `?${dataToURLString(option.query)}` : ''}`,
          },
          runtime: {
            /**
             * @returns OK
             */
            get: (option?: { config?: T | undefined } | undefined) =>
              fetch<Methods_1cgpyyc['get']['resBody'], BasicHeaders, Methods_1cgpyyc['get']['status']>(prefix, PATH21, GET, option).json(),
            /**
             * @returns OK
             */
            $get: (option?: { config?: T | undefined } | undefined) =>
              fetch<Methods_1cgpyyc['get']['resBody'], BasicHeaders, Methods_1cgpyyc['get']['status']>(prefix, PATH21, GET, option).json().then(r => r.body),
            $path: () => `${prefix}${PATH21}`,
          },
        },
      },
    },
  };
};

export type ApiInstance = ReturnType<typeof api>;
export default api;
